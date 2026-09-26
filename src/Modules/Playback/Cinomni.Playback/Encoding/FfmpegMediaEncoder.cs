using System.Diagnostics;
using Cinomni.Playback.Contracts;
using Microsoft.Extensions.Logging;

namespace Cinomni.Playback.Encoding;

/// <summary>
/// One FFmpeg launch: the process still running with a manifest to serve, or — when it exited without
/// one — no process, its exit code and the end of its stderr.
/// </summary>
internal sealed record Attempt(FfmpegProcess? Process, string ManifestPath, int? ExitCode, string Diagnostics);

/// <summary>
/// Production <see cref="IMediaEncoder"/>: spawns FFmpeg to produce a just-in-time HLS ladder. Built
/// against hostile input: an <b>argv</b> list (never a concatenated command), no shell,
/// every codec constrained to a closed whitelist, and the input/output passed as bare arguments
/// confined to validated directories. FFmpeg runs as an isolated child, so a crash or a hostile file
/// cannot take the backend down. Not exercised by unit tests (FFmpeg is a deployment concern); the
/// pure argv it runs, built by <see cref="FfmpegCommand"/>, is.
/// </summary>
public sealed class FfmpegMediaEncoder(
    FfmpegEncoderOptions options,
    HardwareCapabilityProbeOptions hardwareOptions,
    ILogger<FfmpegMediaEncoder> logger) : IMediaEncoder
{
    /// <summary>
    /// Runs the planned backend; if it is hardware and exits before producing a manifest, retries
    /// exactly once, forced to software — a bounded fallback, never a loop. A backend that is already
    /// software, or one that succeeds, never retries. An attempt that exits without a manifest leaves
    /// nothing running, and if the last one does, the start throws.
    /// </summary>
    public async Task<TranscodeOutput> StartHlsAsync(TranscodeRequest request, CancellationToken cancellationToken = default)
    {
        var videoCodec = Whitelisted(request.VideoCodec, options.AllowedVideoCodecs, "video");
        var audioCodec = Whitelisted(request.AudioCodec, options.AllowedAudioCodecs, "audio");

        var attempt = await RunAttemptAsync(request, videoCodec, audioCodec, request.Backend, cancellationToken);
        if (attempt.Process is { } process)
        {
            return new TranscodeOutput(attempt.ManifestPath, request.Backend, process);
        }

        if (request.Backend == EncoderBackend.Software)
        {
            throw NeverStarted(attempt);
        }

        // No stderr in the log: it quotes the media path. It reaches the job row if the retry fails too.
        logger.LogWarning(
            "Hardware backend {Backend} exited (code {Code}) before producing a manifest; retrying once with software.",
            request.Backend, attempt.ExitCode);

        // Clear whatever the failed attempt left behind (a partial manifest, stray segments) so the
        // retry cannot be confused by artifacts from an encode that never actually succeeded.
        ClearFailedAttempt(request.OutputDirectory);

        var fallback = await RunAttemptAsync(request, videoCodec, audioCodec, EncoderBackend.Software, cancellationToken);
        return fallback.Process is { } fallbackProcess
            ? new TranscodeOutput(fallback.ManifestPath, EncoderBackend.Software, fallbackProcess)
            : throw NeverStarted(fallback);
    }

    /// <summary>
    /// The failure a start reports when its last attempt never wrote a manifest: the exit code in the
    /// message, and the end of FFmpeg's stderr kept apart for the job row.
    /// </summary>
    private static TranscodeStartFailedException NeverStarted(Attempt attempt) =>
        new(attempt.ExitCode, attempt.Diagnostics);

    /// <summary>
    /// Removes the files a failed attempt wrote into its own output directory — FFmpeg writes nothing
    /// deeper — without recursing, and refuses a directory that is a link rather than follow it.
    /// </summary>
    private static void ClearFailedAttempt(string outputDirectory)
    {
        if (!Directory.Exists(outputDirectory))
        {
            return;
        }

        if (File.ResolveLinkTarget(outputDirectory, returnFinalTarget: false) is not null)
        {
            throw new InvalidOperationException("The transcode output directory is a link; refusing to write through it.");
        }

        foreach (var file in Directory.EnumerateFiles(outputDirectory))
        {
            File.Delete(file);
        }
    }

    /// <summary>
    /// Starts one attempt and waits for its manifest. Hands back the running process when there is
    /// one to serve from; otherwise the process has already been disposed, and the attempt carries its
    /// exit code and the end of its stderr instead.
    /// </summary>
    private async Task<Attempt> RunAttemptAsync(
        TranscodeRequest request, string videoCodec, string audioCodec, EncoderBackend backend, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(request.OutputDirectory);
        var manifestPath = Path.Combine(request.OutputDirectory, FfmpegCommand.ManifestName);

        var startInfo = new ProcessStartInfo
        {
            FileName = options.BinaryPath,
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        // argv, not a command string — shell metacharacters in any value are inert. The codecs passed
        // the whitelist above; everything else FfmpegCommand builds from closed switches and integers.
        var whitelisted = request with { VideoCodec = videoCodec, AudioCodec = audioCodec };
        foreach (var arg in FfmpegCommand.Transcode(whitelisted, backend, options.SegmentSeconds, hardwareOptions.DevicePath))
        {
            startInfo.ArgumentList.Add(arg);
        }

        var process = FfmpegProcess.Start(startInfo);

        // The client streams segments as FFmpeg writes them (JIT). We only wait for the manifest to
        // appear so the caller can hand back a playable URL; the process keeps running and the caller
        // owns it from here. Any way out of this wait other than a manifest ends the process first:
        // a cancelled request must not leave an encoder running that nothing will ever stop.
        bool appeared;
        try
        {
            appeared = await WaitForManifestAsync(process, manifestPath, cancellationToken);
        }
        catch
        {
            await process.DisposeAsync();
            throw;
        }

        if (appeared)
        {
            return new Attempt(process, manifestPath, null, string.Empty);
        }

        await process.DisposeAsync();
        return new Attempt(null, manifestPath, process.ExitCode, process.Diagnostics);
    }

    /// <summary>
    /// True once the manifest exists, or once the poll window elapses with the process still
    /// running — first hardware init can take longer than the poll window, so that case is optimistic
    /// rather than a failure. False only when FFmpeg is observed to have exited without ever writing
    /// one: the one signal <see cref="StartHlsAsync"/> treats as this attempt having failed.
    /// </summary>
    private static async Task<bool> WaitForManifestAsync(
        FfmpegProcess process, string manifestPath, CancellationToken cancellationToken)
    {
        for (var i = 0; i < 50; i++)
        {
            if (File.Exists(manifestPath))
            {
                return true;
            }

            if (process.HasExited)
            {
                // Checked again: a short remux can write its manifest and exit between the two checks.
                return File.Exists(manifestPath);
            }

            await Task.Delay(100, cancellationToken);
        }

        return true;
    }

    private static string Whitelisted(string codec, IReadOnlySet<string> allowed, string kind)
    {
        if (!allowed.Contains(codec))
        {
            throw new ArgumentException($"Refusing to pass non-whitelisted {kind} codec '{codec}' to FFmpeg.");
        }

        return codec;
    }
}
