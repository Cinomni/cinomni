using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Cinomni.Import.Contracts;
using Microsoft.Extensions.Logging;

namespace Cinomni.Import.Probe;

/// <summary>
/// Production <see cref="IMediaProbe"/>: runs ffprobe as an isolated child process and parses its
/// JSON. Hardened against hostile input: an <b>argv list</b> (never a concatenated
/// command), <c>UseShellExecute=false</c> (shell metacharacters are inert), the file passed as a
/// bare argument, and a timeout that kills a runaway process. Any failure degrades to
/// <see cref="MediaInfo.Empty"/> so a hostile or unreadable file cannot break the import.
/// </summary>
public sealed class FfprobeMediaProbe(FfprobeOptions options, ILogger<FfprobeMediaProbe> logger) : IMediaProbe
{
    public async Task<MediaInfo> ProbeAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            return await RunAsync(filePath, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Probe failure is not fatal: register the asset without streams and move on.
            logger.LogWarning(ex, "ffprobe failed for {FilePath}; registering without media streams.", filePath);
            return MediaInfo.Empty;
        }
    }

    private async Task<MediaInfo> RunAsync(string filePath, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = options.BinaryPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        // argv, not a command string: shell metacharacters in the path are inert.
        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-print_format");
        startInfo.ArgumentList.Add("json");
        startInfo.ArgumentList.Add("-show_format");
        startInfo.ArgumentList.Add("-show_streams");
        startInfo.ArgumentList.Add(filePath);

        using var process = new Process { StartInfo = startInfo };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout);

        process.Start();
        // Drain both pipes concurrently: leaving stderr unread risks the child blocking on a full
        // pipe buffer (a classic redirect deadlock) even though we only parse stdout.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            logger.LogWarning("ffprobe timed out after {Timeout} for {FilePath}.", options.Timeout, filePath);
            return MediaInfo.Empty;
        }

        var json = await stdoutTask;
        await stderrTask;
        return process.ExitCode == 0 ? Parse(json) : MediaInfo.Empty;
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

    private static MediaInfo Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var container = string.Empty;
        double duration = 0;
        long bitrate = 0;
        if (root.TryGetProperty("format", out var format))
        {
            container = GetString(format, "format_name") ?? string.Empty;
            duration = ParseDouble(GetString(format, "duration"));
            bitrate = (long)ParseDouble(GetString(format, "bit_rate"));
        }

        var streams = new List<MediaStreamInfo>();
        if (root.TryGetProperty("streams", out var streamArray) && streamArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var stream in streamArray.EnumerateArray())
            {
                if (TryParseStream(stream, out var info))
                {
                    streams.Add(info);
                }
            }
        }

        return new MediaInfo(container, duration, bitrate, streams);
    }

    private static bool TryParseStream(JsonElement stream, out MediaStreamInfo info)
    {
        info = null!;
        var kindName = GetString(stream, "codec_type");
        var kind = kindName switch
        {
            "video" => MediaStreamKind.Video,
            "audio" => MediaStreamKind.Audio,
            "subtitle" => MediaStreamKind.Subtitle,
            _ => (MediaStreamKind?)null,
        };
        if (kind is null)
        {
            return false;
        }

        var index = stream.TryGetProperty("index", out var indexElement) && indexElement.TryGetInt32(out var i) ? i : 0;
        string? language = null;
        if (stream.TryGetProperty("tags", out var tags))
        {
            language = GetString(tags, "language");
        }

        var disposition = stream.TryGetProperty("disposition", out var d) ? d : default;
        info = new MediaStreamInfo(
            index,
            kind.Value,
            GetString(stream, "codec_name") ?? "unknown",
            language,
            GetInt(stream, "width"),
            GetInt(stream, "height"),
            GetInt(stream, "channels"),
            IsDisposition(disposition, "default"),
            IsDisposition(disposition, "forced"),
            kind == MediaStreamKind.Video ? VideoRangeOf(stream) : null);
        return true;
    }

    /// <summary>
    /// A video stream's dynamic range: Dolby Vision by its configuration record in the side data, HDR10
    /// by the PQ transfer function, HLG by its own; anything else is SDR. ffprobe reports both with
    /// <c>-show_streams</c>, so nothing more is read from the file.
    /// </summary>
    internal static string VideoRangeOf(JsonElement stream)
    {
        if (stream.TryGetProperty("side_data_list", out var sideData) && sideData.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in sideData.EnumerateArray())
            {
                if (GetString(entry, "side_data_type") is { } type && type.Contains("DOVI", StringComparison.OrdinalIgnoreCase))
                {
                    return "DoVi";
                }
            }
        }

        return GetString(stream, "color_transfer") switch
        {
            "smpte2084" => "Hdr10",
            "arib-std-b67" => "Hlg",
            _ => "Sdr",
        };
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var i)
            ? i
            : null;

    private static bool IsDisposition(JsonElement disposition, string name) =>
        disposition.ValueKind == JsonValueKind.Object
        && disposition.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var flag)
        && flag == 1;

    private static double ParseDouble(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : 0;
}
