using System.Collections.Concurrent;
using Cinomni.Import.Contracts;
using Cinomni.Import.Files;
using Cinomni.Import.Probe;

namespace Cinomni.SeriesSlice.Tests;

/// <summary>
/// In-memory <see cref="IImportFileSystem"/> for the acceptance host: it scripts the staged content of
/// a completed download and records every hardlink, so "one download landed N episode files" is
/// observable without touching disk.
/// </summary>
internal sealed class FakeSliceFileSystem : IImportFileSystem
{
    private readonly ConcurrentDictionary<string, long> _files = new();
    private readonly ConcurrentDictionary<string, List<ImportFileEntry>> _contents = new();

    /// <summary>Every (source, destination) pair the import linked, in order.</summary>
    public List<(string From, string To)> Linked { get; } = [];

    /// <summary>Registers the files that live under a staged content path.</summary>
    public void SeedContent(string contentPath, params ImportFileEntry[] entries)
    {
        _contents[contentPath] = [.. entries];
        foreach (var entry in entries)
        {
            _files[entry.Path] = entry.Size;
        }
    }

    public bool DirectoryExists(string path) => ContentOf(path) is not null;

    public bool FileExists(string path) => _files.ContainsKey(path);

    /// <summary>Two files are the same content here when both exist with the same size.</summary>
    public Task<bool> HasSameContentAsync(string first, string second, CancellationToken cancellationToken = default) =>
        Task.FromResult(_files.TryGetValue(first, out var a) && _files.TryGetValue(second, out var b) && a == b);

    public bool RootAccessible(string root) => true;

    public long GetSize(string path) => _files.TryGetValue(path, out var size) ? size : 0;

    /// <summary>The folders the engine fake was told to save into; content only ever lands in one of them.</summary>
    public Func<IEnumerable<string>>? SaveFolders { get; set; }

    /// <summary>
    /// The files of a content path. A download writes into a folder of its own inside the staging area
    /// (<c>staging/&lt;attempt&gt;/&lt;name&gt;</c>), named only when the download is added, so a test
    /// seeds <c>staging/&lt;name&gt;</c>; the first time a path inside a folder the engine was given is
    /// asked for, the seeded tree moves there, as the torrent would have written it.
    /// </summary>
    private List<ImportFileEntry>? ContentOf(string contentPath)
    {
        if (_contents.TryGetValue(contentPath, out var entries))
        {
            return entries;
        }

        var segments = contentPath.Split('/');
        if (segments.Length < 3 || !Guid.TryParse(segments[^2], out _))
        {
            return null;
        }

        // Only into a folder the engine was told to save into: a content path pointing anywhere else
        // is a mismatch between where the download writes and where the import reads, and must find
        // nothing, exactly as it would on disk.
        var saveFolder = string.Join('/', segments[..^1]);
        if (SaveFolders is { } known && !known().Contains(saveFolder))
        {
            return null;
        }

        var seeded = string.Join('/', segments[..^2]) + "/" + segments[^1];
        if (!_contents.TryRemove(seeded, out var original))
        {
            return null;
        }

        var moved = original
            .Select(entry => entry with { Path = contentPath + entry.Path[seeded.Length..] })
            .ToList();
        foreach (var (before, after) in original.Zip(moved))
        {
            _files.TryRemove(before.Path, out _);
            _files[after.Path] = after.Size;
        }

        _contents[contentPath] = moved;
        return moved;
    }

    public IReadOnlyList<ImportFileEntry> EnumerateFiles(string contentPath)
    {
        if (ContentOf(contentPath) is { } entries)
        {
            return entries;
        }

        return _files.TryGetValue(contentPath, out var size)
            ? [new ImportFileEntry(contentPath, size)]
            : [];
    }

    public Task<string> ComputeFingerprintAsync(string path, CancellationToken cancellationToken = default) =>
        Task.FromResult($"fp-{path}");

    public Task EnsureDirectoryAsync(string directory, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<FileOperationType> HardlinkOrCopyAsync(
        string fromPath,
        string toPath,
        CancellationToken cancellationToken = default)
    {
        Linked.Add((fromPath, toPath));
        _files[toPath] = _files.TryGetValue(fromPath, out var size) ? size : 0;
        // The slice runs on the storage contract holding, which is what a hardlink reports.
        return Task.FromResult(FileOperationType.Hardlink);
    }

    public Task DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        _files.TryRemove(path, out _);
        return Task.CompletedTask;
    }

    public Task MoveAsync(string fromPath, string toPath, CancellationToken cancellationToken = default)
    {
        if (_files.TryRemove(fromPath, out var size))
        {
            _files[toPath] = size;
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Scripted <see cref="IMediaProbe"/>: ffprobe is absent in dev and is a deployment concern,
/// so every landed file reports the same synthetic stream set. The acceptance suite asserts on the
/// spine, not on codec extraction, which the Import suite already covers per file.
/// </summary>
internal sealed class FakeSliceMediaProbe : IMediaProbe
{
    /// <summary>Every path handed to the probe, in order.</summary>
    public List<string> Probed { get; } = [];

    public Task<MediaInfo> ProbeAsync(string filePath, CancellationToken cancellationToken = default)
    {
        Probed.Add(filePath);
        return Task.FromResult(new MediaInfo(
            "matroska,webm",
            DurationSeconds: 3000,
            Bitrate: 4_000_000,
            Streams:
            [
                new MediaStreamInfo(0, MediaStreamKind.Video, "h264", null, 1920, 1080, null, IsDefault: true, IsForced: false),
                new MediaStreamInfo(1, MediaStreamKind.Audio, "aac", "eng", null, null, 6, IsDefault: true, IsForced: false),
            ]));
    }
}
