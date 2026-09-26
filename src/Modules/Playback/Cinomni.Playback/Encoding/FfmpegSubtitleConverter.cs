using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using TextEncoding = System.Text.Encoding;

namespace Cinomni.Playback.Encoding;

/// <summary>
/// Production <see cref="ISubtitleConverter"/>. FFmpeg runs as a child with an <b>argv</b> list (no
/// shell); its only inputs are a library path Library validated at import or text piped to its stdin,
/// and its only output is stdout. Every run is bounded: at most <see cref="MaxConcurrent"/> at once —
/// pulling one stream out of a film reads the whole file — no longer than <see cref="Timeout"/>, and no
/// more than <see cref="MaxOutputChars"/> of WebVTT. Past any bound the process tree is killed and the
/// answer is null.
/// </summary>
public sealed class FfmpegSubtitleConverter(FfmpegEncoderOptions options, ILogger<FfmpegSubtitleConverter> logger)
    : ISubtitleConverter, IDisposable
{
    private const int MaxConcurrent = 2;
    private const int MaxOutputChars = 16 * 1024 * 1024;
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    private readonly SemaphoreSlim _slots = new(MaxConcurrent, MaxConcurrent);

    public Task<string?> ExtractToWebVttAsync(string mediaPath, int streamIndex, CancellationToken cancellationToken = default) =>
        RunAsync(
            [
                "-nostdin", "-i", mediaPath,
                "-map", "0:" + streamIndex.ToString(CultureInfo.InvariantCulture),
                "-c:s", "webvtt", "-f", "webvtt", "pipe:1",
            ],
            stdin: null,
            cancellationToken);

    public Task<string?> AssToWebVttAsync(string assText, CancellationToken cancellationToken = default) =>
        RunAsync(["-f", "ass", "-i", "pipe:0", "-c:s", "webvtt", "-f", "webvtt", "pipe:1"], assText, cancellationToken);

    public void Dispose() => _slots.Dispose();

    private async Task<string?> RunAsync(IReadOnlyList<string> args, string? stdin, CancellationToken cancellationToken)
    {
        await _slots.WaitAsync(cancellationToken);
        try
        {
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bounded.CancelAfter(Timeout);
            return await RunBoundedAsync(args, stdin, bounded.Token, cancellationToken);
        }
        finally
        {
            _slots.Release();
        }
    }

    private async Task<string?> RunBoundedAsync(
        IReadOnlyList<string> args, string? stdin, CancellationToken bounded, CancellationToken caller)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = options.BinaryPath,
            UseShellExecute = false,
            RedirectStandardInput = stdin is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = TextEncoding.UTF8,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("error");
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
            logger.LogWarning("FFmpeg could not be started to convert a subtitle: {Reason}", ex.Message);
            return null;
        }

        try
        {
            // stderr is drained and dropped: it can quote the media path, and a full pipe would stall FFmpeg.
            var drain = process.StandardError.ReadToEndAsync(bounded);
            var feed = stdin is null ? Task.CompletedTask : FeedAsync(process.StandardInput, stdin, bounded);
            var output = await ReadBoundedAsync(process.StandardOutput, bounded);
            if (output is null)
            {
                logger.LogWarning("Subtitle conversion passed its size bound and was stopped.");
                return null;
            }

            await feed;
            await process.WaitForExitAsync(bounded);
            await drain;
            if (process.ExitCode != 0)
            {
                logger.LogWarning("Subtitle conversion failed with exit code {Code}.", process.ExitCode);
                return null;
            }

            return output;
        }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested)
        {
            logger.LogWarning("Subtitle conversion took longer than {Timeout} and was stopped.", Timeout);
            return null;
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

    private static async Task FeedAsync(StreamWriter input, string text, CancellationToken cancellationToken)
    {
        try
        {
            await input.WriteAsync(text.AsMemory(), cancellationToken);
        }
        catch (IOException)
        {
            // FFmpeg stopped reading because it failed on the input: its exit code says so.
        }
        finally
        {
            input.Close();
        }
    }

    /// <summary>All of stdout, or null once it passes the size bound.</summary>
    private static async Task<string?> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var buffer = new char[16 * 1024];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            if (builder.Length + read > MaxOutputChars)
            {
                return null;
            }

            builder.Append(buffer, 0, read);
        }

        return builder.ToString();
    }
}
