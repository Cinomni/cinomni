using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Cinomni.Library.Contracts;
using Microsoft.Extensions.Logging;

namespace Cinomni.Playback.Encoding;

/// <summary>
/// Reads a video's dynamic range when Library does not know it — a file imported before Import read
/// it. The answer is cached per version for the life of the process; a file does not change its range.
/// </summary>
public interface IVideoRangeProbe
{
    /// <summary>The range of the first video stream, or null when it cannot be told.</summary>
    Task<VideoRangeType?> ProbeAsync(Guid versionId, string path, CancellationToken cancellationToken = default);
}

/// <summary>The probe a host without FFmpeg gets: it never knows, so the file is taken for SDR.</summary>
public sealed class UnknownVideoRangeProbe : IVideoRangeProbe
{
    public Task<VideoRangeType?> ProbeAsync(Guid versionId, string path, CancellationToken cancellationToken = default) =>
        Task.FromResult<VideoRangeType?>(null);
}

/// <summary>
/// Production <see cref="IVideoRangeProbe"/>: asks ffprobe for the first video stream's transfer
/// function and side data only — the container header, not the file. Argv, no shell, a bounded time.
/// </summary>
public sealed class FfprobeVideoRangeProbe(FfmpegEncoderOptions options, ILogger<FfprobeVideoRangeProbe> logger)
    : IVideoRangeProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private const int MaxCachedVersions = 4096;

    private readonly ConcurrentDictionary<Guid, VideoRangeType?> _known = new();

    public async Task<VideoRangeType?> ProbeAsync(Guid versionId, string path, CancellationToken cancellationToken = default)
    {
        if (_known.TryGetValue(versionId, out var cached))
        {
            return cached;
        }

        var range = await RunAsync(path, cancellationToken);
        if (_known.Count < MaxCachedVersions)
        {
            _known[versionId] = range;
        }

        return range;
    }

    private async Task<VideoRangeType?> RunAsync(string path, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = options.ProbeBinaryPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in (string[])
                 [
                     "-v", "error", "-select_streams", "v:0", "-print_format", "json",
                     "-show_entries", "stream=color_transfer:stream_side_data=side_data_type", path,
                 ])
        {
            startInfo.ArgumentList.Add(arg);
        }

        try
        {
            using var process = new Process { StartInfo = startInfo };
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bounded.CancelAfter(Timeout);
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync(bounded.Token);
            var stderr = process.StandardError.ReadToEndAsync(bounded.Token);
            try
            {
                await process.WaitForExitAsync(bounded.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            await stderr;
            return process.ExitCode == 0 ? Parse(await stdout) : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or JsonException)
        {
            // Never the path: it is the viewer's library layout.
            logger.LogWarning("Could not read a video's dynamic range: {Reason}", ex.GetType().Name);
            return null;
        }
    }

    /// <summary>Pure: ffprobe's JSON for one stream to a range — Dolby Vision by its side data, else by the transfer function.</summary>
    internal static VideoRangeType? Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("streams", out var streams)
            || streams.ValueKind != JsonValueKind.Array
            || streams.GetArrayLength() == 0)
        {
            return null;
        }

        return VideoRanges.From(streams[0]);
    }
}

/// <summary>The one reading of an ffprobe stream's dynamic range, shared by Import's probe and Playback's.</summary>
public static class VideoRanges
{
    /// <summary>Dolby Vision by its configuration record, HDR10 by PQ, HLG by its curve; anything else is SDR.</summary>
    public static VideoRangeType From(JsonElement stream)
    {
        if (stream.TryGetProperty("side_data_list", out var sideData) && sideData.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in sideData.EnumerateArray())
            {
                if (entry.TryGetProperty("side_data_type", out var type)
                    && type.GetString() is { } name
                    && name.Contains("DOVI", StringComparison.OrdinalIgnoreCase))
                {
                    return VideoRangeType.DoVi;
                }
            }
        }

        var transfer = stream.TryGetProperty("color_transfer", out var value) ? value.GetString() : null;
        return transfer switch
        {
            "smpte2084" => VideoRangeType.Hdr10,
            "arib-std-b67" => VideoRangeType.Hlg,
            _ => VideoRangeType.Sdr,
        };
    }
}
