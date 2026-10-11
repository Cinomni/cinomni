using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Cinomni.Subtitles.Sync;

/// <summary>
/// Production <see cref="ISpeechActivitySource"/>. FFmpeg runs as a child with an <b>argv</b> list (no
/// shell). Its inputs are a library path Library validated at import and numbers this module formats
/// itself, and its only output is raw 8 kHz mono PCM on stdout, band-limited to the voice range.
/// Every run is bounded: at most <see cref="SubtitleSyncOptions.Concurrency"/> at once, no longer than
/// <see cref="SubtitleSyncOptions.WindowTimeout"/>, and no more audio than the stretch asked for (plus a
/// little slack). Past any bound the process tree is killed and the stretch reports a failure. stderr
/// is drained and dropped: it can quote the media path, and a full pipe would stall FFmpeg.
/// <para>
/// Playback runs FFmpeg the same way for its subtitle conversions. This is its own copy because a
/// module never references another's implementation; the rules are the same ones (SECURITY.md).
/// </para>
/// </summary>
public sealed class FfmpegSpeechActivitySource(SubtitleSyncOptions options, ILogger<FfmpegSpeechActivitySource> logger)
    : ISpeechActivitySource, IDisposable
{
    /// <summary>Two bytes per sample at 8 kHz.</summary>
    private const int BytesPerSecond = SpeechDetector.SampleRate * 2;

    private const int SlackBytes = 64 * 1024;

    private readonly SemaphoreSlim _slots = new(options.Concurrency, options.Concurrency);

    public async Task<SpeechActivity> DetectAsync(
        string mediaPath,
        int audioStreamIndex,
        TimeSpan start,
        TimeSpan length,
        CancellationToken cancellationToken = default)
    {
        await _slots.WaitAsync(cancellationToken);
        try
        {
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bounded.CancelAfter(options.WindowTimeout);
            var maxBytes = (int)Math.Min(int.MaxValue, (length.TotalSeconds * BytesPerSecond) + SlackBytes);
            var pcm = await RunAsync(Arguments(mediaPath, audioStreamIndex, start, length), maxBytes, bounded.Token, cancellationToken);
            return pcm.Failure is { } failure
                ? SpeechActivity.Failed(failure)
                : SpeechActivity.Found(SpeechDetector.Detect(SpeechDetector.FrameLevels(pcm.Bytes)));
        }
        finally
        {
            _slots.Release();
        }
    }

    public void Dispose() => _slots.Dispose();

    /// <summary>The whole command line, from a closed vocabulary plus the path and three numbers.</summary>
    internal static IReadOnlyList<string> Arguments(string mediaPath, int audioStreamIndex, TimeSpan start, TimeSpan length) =>
    [
        "-hide_banner", "-loglevel", "error", "-nostdin",
        // Seeking before the input is fast: FFmpeg jumps to the nearest point instead of decoding to it.
        "-ss", Seconds(start),
        "-t", Seconds(length),
        "-i", mediaPath,
        "-map", "0:" + audioStreamIndex.ToString(CultureInfo.InvariantCulture),
        "-vn", "-sn", "-dn",
        "-ac", "1",
        "-ar", SpeechDetector.SampleRate.ToString(CultureInfo.InvariantCulture),
        // aresample first: a track whose first sample comes after the video's (an audio delay in the
        // container) is padded with silence back to the seek point, so frame 0 of the output is the
        // window's start and not wherever the audio happens to begin.
        "-af", "aresample=async=1:first_pts=0,highpass=f=150,lowpass=f=3500",
        "-f", "s16le",
        "pipe:1",
    ];

    private static string Seconds(TimeSpan value) =>
        Math.Max(0, value.TotalSeconds).ToString("0.###", CultureInfo.InvariantCulture);

    private async Task<(byte[] Bytes, string? Failure)> RunAsync(
        IReadOnlyList<string> args, int maxBytes, CancellationToken bounded, CancellationToken caller)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = options.FfmpegBinaryPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            logger.LogWarning("FFmpeg could not be started to analyze subtitle timing: {Reason}", ex.Message);
            return ([], "FFmpeg could not be started");
        }

        try
        {
            var drain = DiscardAsync(process.StandardError, bounded);
            var output = await ReadBoundedAsync(process.StandardOutput.BaseStream, maxBytes, bounded);
            if (output is null)
            {
                return ([], "FFmpeg returned more audio than was asked for");
            }

            await process.WaitForExitAsync(bounded);
            await drain;
            return process.ExitCode == 0
                ? (output, null)
                : ([], string.Create(CultureInfo.InvariantCulture, $"FFmpeg exited with code {process.ExitCode}"));
        }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested)
        {
            return ([], string.Create(CultureInfo.InvariantCulture, $"decoding the audio took longer than {options.WindowTimeout.TotalSeconds:0} s"));
        }
        finally
        {
            Kill(process);
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Exited between the check and the kill: nothing left to stop.
        }
    }

    /// <summary>
    /// Reads stderr to the end and keeps none of it. It has to be read — a full pipe would block FFmpeg —
    /// but what it says can quote the media path and is not used, and a hostile file can make FFmpeg
    /// write it as fast as it decodes, so it is never accumulated.
    /// </summary>
    private static async Task DiscardAsync(StreamReader stderr, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        while (await stderr.ReadAsync(buffer.AsMemory(), cancellationToken) > 0)
        {
        }
    }

    /// <summary>All of stdout, or null once it passes <paramref name="maxBytes"/>.</summary>
    private static async Task<byte[]?> ReadBoundedAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
