using System.Collections.Concurrent;
using Cinomni.Import.Contracts;
using Cinomni.Import.Probe;

namespace Cinomni.Import.Tests;

/// <summary>
/// Scripted <see cref="IMediaProbe"/> so import tests assert on stream extraction without a real
/// ffprobe binary (absent in dev; provided at deploy). Set <see cref="Fail"/> to model a
/// probe failure — the module must still register the asset, with empty streams.
/// <para>
/// Results are scriptable <b>per path</b>: a season pack probes N different files, and a fake that
/// answered identically for every one of them could not tell "each file was probed and registered
/// with its own streams" from "one file was probed ten times".
/// </para>
/// </summary>
internal sealed class FakeMediaProbe : IMediaProbe
{
    private readonly ConcurrentDictionary<string, MediaInfo> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _failingPaths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The result for any path with no per-path script.</summary>
    public MediaInfo Result { get; set; } = new(
        "matroska,webm",
        DurationSeconds: 6000,
        Bitrate: 8_000_000,
        Streams:
        [
            new MediaStreamInfo(0, MediaStreamKind.Video, "h264", null, 1920, 1080, null, IsDefault: true, IsForced: false),
            new MediaStreamInfo(1, MediaStreamKind.Audio, "aac", "eng", null, null, 6, IsDefault: true, IsForced: false),
        ]);

    /// <summary>When true, every probe fails regardless of path.</summary>
    public bool Fail { get; set; }

    public List<string> Probed { get; } = [];

    /// <summary>Scripts the result for one landed path (matched on the file name, case-insensitively).</summary>
    public void ScriptForFile(string fileName, MediaInfo mediaInfo) => _byPath[fileName] = mediaInfo;

    /// <summary>Scripts a probe failure for one landed path.</summary>
    public void FailForFile(string fileName) => _failingPaths[fileName] = true;

    public Task<MediaInfo> ProbeAsync(string filePath, CancellationToken cancellationToken = default)
    {
        Probed.Add(filePath);
        var fileName = Path.GetFileName(filePath);
        if (Fail || _failingPaths.ContainsKey(fileName))
        {
            return Task.FromResult(MediaInfo.Empty);
        }

        return Task.FromResult(_byPath.TryGetValue(fileName, out var scripted) ? scripted : Result);
    }
}
