using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Cinomni.Playback.Contracts;
using Microsoft.Extensions.Logging;

namespace Cinomni.Playback.Encoding;

/// <summary>
/// Production <see cref="IHardwareCapabilityProbe"/>. It first asks FFmpeg's own CLI what the build
/// offers (<c>-hwaccels</c>, <c>-encoders</c>, <c>-filters</c>), narrows that to the backends this
/// platform can have — VAAPI, QSV and NVENC on Linux, NVENC, QSV and AMF on Windows — and then
/// <b>tries each one</b>: a short encode of a generated picture per output codec, and a decode of a
/// generated clip through the device's own pipeline. Only what worked is reported.
/// <para>
/// A listed encoder proves nothing about the machine: every FFmpeg build carries <c>h264_nvenc</c>, and
/// taking it at its word made every transcode on a host without an NVIDIA GPU fail once before it fell
/// back to software. VAAPI decode was the opposite case — FFmpeg lists no <c>_vaapi</c> decoders, so
/// reading the decoder list never found it.
/// </para>
/// <para>
/// Same isolated-process discipline as <c>FfprobeMediaProbe</c> — argv, no shell, a bounded timeout per
/// run — and the same failure philosophy: a missing binary, a timeout, or a non-zero exit degrades to
/// "not available" rather than throwing, since hardware transcoding is an optional layer over software
/// HLS and must never be able to fail a startup check.
/// </para>
/// </summary>
public sealed partial class FfmpegHardwareCapabilityProbe(
    HardwareCapabilityProbeOptions options,
    ILogger<FfmpegHardwareCapabilityProbe> logger)
    : IHardwareCapabilityProbe
{
    private const int MaxFailureLength = 240;

    /// <summary>An encoder/decoder/filter-listing line's flag column, e.g. <c>V.....</c>, before the name.</summary>
    [GeneratedRegex(@"^\s*\S{2,6}\s+(\S+)")]
    private static partial Regex CodecLinePattern();

    public async Task<HardwareCapabilities> ProbeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var version = FirstLine((await RunAsync(["-hide_banner", "-version"], cancellationToken)).Stdout);
            var hwaccels = ParseHwaccels((await RunAsync(["-hide_banner", "-hwaccels"], cancellationToken)).Stdout);
            var encoders = ParseEncoders((await RunAsync(["-hide_banner", "-encoders"], cancellationToken)).Stdout);
            var filters = ParseFilters((await RunAsync(["-hide_banner", "-filters"], cancellationToken)).Stdout);

            var candidates = CandidateBackends(
                hwaccels, encoders, Exists(options.DevicePath), Exists(options.NvidiaDevicePath), OperatingSystem.IsWindows());
            var tests = await RunTestsAsync(candidates, encoders, cancellationToken);

            var report = new HardwareProbeReport(
                version,
                $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})",
                DateTimeOffset.UtcNow,
                hwaccels,
                tests);
            return Summarize(tests, encoders, filters) with { Report = report };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Hardware capability probe failed; treating no hardware backend as available.");
            return new HardwareCapabilities([]);
        }
    }

    private static bool Exists(string path) => !string.IsNullOrWhiteSpace(path) && File.Exists(path);

    /// <summary>Encode tests for every candidate and codec, then decode tests for every backend whose encode worked.</summary>
    private async Task<IReadOnlyList<ProbeTestResult>> RunTestsAsync(
        IReadOnlyList<EncoderBackend> candidates, IReadOnlyList<string> encoders, CancellationToken cancellationToken)
    {
        var results = new List<ProbeTestResult>();
        if (candidates.Count == 0)
        {
            return results;
        }

        foreach (var backend in candidates)
        {
            foreach (var codec in (VideoOutputCodec[])[VideoOutputCodec.H264, VideoOutputCodec.Hevc])
            {
                var encoder = FfmpegCommand.EncoderName(backend, codec);
                if (!encoders.Contains(encoder, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                var run = await RunAsync(FfmpegCommand.EncodeTest(backend, codec, options.DevicePath), cancellationToken);
                results.Add(new ProbeTestResult(
                    backend, ProbeTestKind.Encode, CodecName(codec), run.ExitCode == 0, Failure(run, null)));
            }
        }

        var workspace = Path.Combine(Path.GetTempPath(), "cinomni-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            foreach (var codec in (VideoOutputCodec[])[VideoOutputCodec.H264, VideoOutputCodec.Hevc])
            {
                var sample = Path.Combine(workspace, $"sample-{CodecName(codec)}.mp4");
                if ((await RunAsync(FfmpegCommand.Sample(codec, sample), cancellationToken)).ExitCode != 0)
                {
                    continue; // No software encoder to make the clip with (a build without libx265).
                }

                foreach (var backend in candidates.Where(b => results.Any(r => r is { Kind: ProbeTestKind.Encode, Passed: true, Codec: "h264" } && r.Backend == b)))
                {
                    if (FfmpegCommand.DecodeTest(backend, VideoOutputCodec.H264, sample, options.DevicePath) is not { } args)
                    {
                        continue;
                    }

                    var run = await RunAsync(args, cancellationToken);
                    results.Add(new ProbeTestResult(
                        backend, ProbeTestKind.Decode, CodecName(codec), run.ExitCode == 0, Failure(run, workspace)));
                }
            }
        }
        finally
        {
            TryDelete(workspace);
        }

        return results;
    }

    private static string CodecName(VideoOutputCodec codec) => codec == VideoOutputCodec.Hevc ? "hevc" : "h264";

    /// <summary>The last thing FFmpeg complained about, with the sample's directory taken out; null when it succeeded.</summary>
    private static string? Failure(RunResult run, string? workspace)
    {
        if (run.ExitCode == 0)
        {
            return null;
        }

        var line = run.Stderr
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault() ?? (run.TimedOut ? "timed out" : $"exited with code {run.ExitCode}");
        if (workspace is not null)
        {
            line = line.Replace(workspace, "<sample>", StringComparison.Ordinal);
        }

        return line.Length > MaxFailureLength ? line[..MaxFailureLength] : line;
    }

    private static void TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A few kilobytes in the temp directory; the OS reclaims it.
        }
    }

    private sealed record RunResult(int ExitCode, string Stdout, string Stderr, bool TimedOut);

    private async Task<RunResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = options.BinaryPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        // argv, not a command string: shell metacharacters are inert, and every argument is fixed by
        // this class or FfmpegCommand (never user/request input).
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout);

        process.Start();
        // Drain both pipes concurrently: leaving one unread risks the child blocking on a full buffer.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            logger.LogWarning("ffmpeg {Argument} timed out after {Timeout}.", arguments.LastOrDefault(), options.Timeout);
            return new RunResult(-1, string.Empty, string.Empty, TimedOut: true);
        }

        return new RunResult(process.ExitCode, await stdoutTask, await stderrTask, TimedOut: false);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The process already exited between the check and the kill — nothing to do.
        }
    }

    private static string FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "unknown";

    /// <summary>
    /// Pure: every non-empty, non-header line of <c>ffmpeg -hwaccels</c> output is one accelerator
    /// name (e.g. <c>vaapi</c>, <c>cuda</c>, <c>qsv</c>).
    /// </summary>
    internal static IReadOnlyList<string> ParseHwaccels(string stdout)
    {
        var names = new List<string>();
        foreach (var rawLine in stdout.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.EndsWith(':'))
            {
                continue;
            }

            names.Add(line);
        }

        return names;
    }

    /// <summary>Pure: the encoder names of <c>ffmpeg -encoders</c> output (see <see cref="ParseTable"/>).</summary>
    internal static IReadOnlyList<string> ParseEncoders(string stdout) => ParseTable(stdout);

    /// <summary>Pure: the decoder names of <c>ffmpeg -decoders</c> output (see <see cref="ParseTable"/>).</summary>
    internal static IReadOnlyList<string> ParseDecoders(string stdout) => ParseTable(stdout);

    /// <summary>Pure: the filter names of <c>ffmpeg -filters</c> output (see <see cref="ParseTable"/>).</summary>
    internal static IReadOnlyList<string> ParseFilters(string stdout) => ParseTable(stdout);

    /// <summary>
    /// Pure: the row-parsing shared by <c>-encoders</c>, <c>-decoders</c> and <c>-filters</c>. Their
    /// legend above the first row has the same flag-column shape as a real row (e.g. <c> V..... = Video</c>),
    /// so rows are only read after the <c>------</c> separator (codec tables) or the <c>= </c> legend's
    /// end (filters, whose legend has no separator and whose rows carry an arrow).
    /// </summary>
    private static IReadOnlyList<string> ParseTable(string stdout)
    {
        var names = new List<string>();
        var pastLegend = false;
        var isFilterTable = stdout.Contains("->", StringComparison.Ordinal);

        foreach (var rawLine in stdout.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (isFilterTable)
            {
                // A filter row: flags, name, pads ("V->V"), description. The legend rows have no arrow.
                if (!line.Contains("->", StringComparison.Ordinal))
                {
                    continue;
                }
            }
            else if (!pastLegend)
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 0 && trimmed.All(c => c == '-'))
                {
                    pastLegend = true;
                }

                continue;
            }

            var match = CodecLinePattern().Match(line);
            if (match.Success)
            {
                names.Add(match.Groups[1].Value);
            }
        }

        return names;
    }

    /// <summary>
    /// Pure: the backends worth testing on this platform. On Linux every backend also needs its device:
    /// VAAPI/QSV the render node, NVENC the NVIDIA control node the container toolkit creates when a GPU
    /// is granted. On Windows there are no device nodes to look at, so the listed encoder is enough to
    /// be worth a test — which is what decides.
    /// </summary>
    internal static IReadOnlyList<EncoderBackend> CandidateBackends(
        IReadOnlyList<string> hwaccels,
        IReadOnlyList<string> encoders,
        bool devicePathExists,
        bool nvidiaDevicePresent,
        bool isWindows)
    {
        if (!isWindows)
        {
            return DetermineAvailableBackends(hwaccels, encoders, devicePathExists, nvidiaDevicePresent);
        }

        var backends = new List<EncoderBackend>();
        if (encoders.Contains("h264_nvenc", StringComparer.OrdinalIgnoreCase))
        {
            backends.Add(EncoderBackend.Nvenc);
        }

        if (encoders.Contains("h264_qsv", StringComparer.OrdinalIgnoreCase))
        {
            backends.Add(EncoderBackend.Qsv);
        }

        if (encoders.Contains("h264_amf", StringComparer.OrdinalIgnoreCase))
        {
            backends.Add(EncoderBackend.Amf);
        }

        return backends;
    }

    /// <summary>
    /// Pure, Linux: maps raw FFmpeg tokens to the backends worth testing. Every backend needs its device
    /// as well as its encoder: VAAPI/QSV their render node, NVENC the NVIDIA control node.
    /// </summary>
    internal static IReadOnlyList<EncoderBackend> DetermineAvailableBackends(
        IReadOnlyList<string> hwaccels, IReadOnlyList<string> encoders, bool devicePathExists, bool nvidiaDevicePresent)
    {
        var backends = new List<EncoderBackend>();

        if (devicePathExists
            && hwaccels.Contains("vaapi", StringComparer.OrdinalIgnoreCase)
            && encoders.Contains("h264_vaapi", StringComparer.OrdinalIgnoreCase))
        {
            backends.Add(EncoderBackend.Vaapi);
        }

        if (devicePathExists && encoders.Contains("h264_qsv", StringComparer.OrdinalIgnoreCase))
        {
            backends.Add(EncoderBackend.Qsv);
        }

        if (nvidiaDevicePresent && encoders.Contains("h264_nvenc", StringComparer.OrdinalIgnoreCase))
        {
            backends.Add(EncoderBackend.Nvenc);
        }

        return backends;
    }

    /// <summary>
    /// Pure: what the tests proved. A backend is available when its H.264 encode worked; it encodes HEVC
    /// when that test worked too; it decodes a codec when the device-pipeline decode of that codec worked.
    /// The build's software HEVC encoder and the filters tone mapping and burn-in need are read off its
    /// own listings.
    /// </summary>
    internal static HardwareCapabilities Summarize(
        IReadOnlyList<ProbeTestResult> tests, IReadOnlyList<string> encoders, IReadOnlyList<string> filters)
    {
        var passed = tests.Where(t => t.Passed).ToList();
        var available = passed
            .Where(t => t is { Kind: ProbeTestKind.Encode, Codec: "h264" })
            .Select(t => t.Backend)
            .Distinct()
            .ToList();
        var encodable = passed
            .Where(t => t.Kind == ProbeTestKind.Encode && available.Contains(t.Backend))
            .Select(t => new EncodeCapability(t.Backend, t.Codec == "hevc" ? VideoOutputCodec.Hevc : VideoOutputCodec.H264))
            .Distinct()
            .ToList();
        var decodable = passed
            .Where(t => t.Kind == ProbeTestKind.Decode && available.Contains(t.Backend))
            .Select(t => new DecodeCapability(t.Backend, t.Codec))
            .Distinct()
            .ToList();

        bool Has(IReadOnlyList<string> names, string name) => names.Contains(name, StringComparer.OrdinalIgnoreCase);

        return new HardwareCapabilities(available, decodable)
        {
            EncodableCodecs = encodable,
            SoftwareHevc = Has(encoders, "libx265"),
            ToneMapping = Has(filters, "zscale") && Has(filters, "tonemap"),
            SubtitleOverlay = Has(filters, "overlay"),
        };
    }
}
