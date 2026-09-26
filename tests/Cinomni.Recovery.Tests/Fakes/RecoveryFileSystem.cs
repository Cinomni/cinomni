using System.Collections.Concurrent;
using Cinomni.Import.Contracts;
using Cinomni.Import.Files;

namespace Cinomni.Recovery.Tests.Fakes;

/// <summary>
/// In-memory <see cref="IImportFileSystem"/> for the recovery suite. Like the engine fake it outlives
/// the service provider, because a restart destroys the process and not the disk: a filesystem
/// rebuilt with the provider would forget the very files whose survival scenario 15 is about.
/// <para>
/// It adds the one thing the module suites cannot express — a crash <b>inside</b> a drive.
/// <see cref="ThrowOnLinkNumber"/> makes the nth hardlink of a run fail with an exception the import
/// does not catch, so the drive dies exactly where a process would, with earlier files already on
/// disk and nothing committed.
/// </para>
/// </summary>
internal sealed class RecoveryFileSystem : IImportFileSystem
{
    private readonly ConcurrentDictionary<string, long> _files = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, List<ImportFileEntry>> _contents = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _failingTargets = new(StringComparer.OrdinalIgnoreCase);

    private int _links;
    private int _crashOnLink;

    /// <summary>Every hardlink attempted, as (from, to), across the whole run including restarts.</summary>
    public ConcurrentQueue<(string From, string To)> Linked { get; } = [];

    /// <summary>Every file set aside to make room for a replacement.</summary>
    public ConcurrentQueue<(string From, string To)> Moved { get; } = [];

    /// <summary>Every path a rollback removed.</summary>
    public ConcurrentQueue<string> Deleted { get; } = [];

    /// <summary>Whether the library root answers as mounted and writable.</summary>
    public bool RootIsAccessible { get; set; } = true;

    /// <summary>
    /// Makes the nth hardlink of the run die with an exception the import does not handle, which is
    /// how a process disappearing mid-drive presents: earlier links are on disk, later ones never
    /// happened, and the drive's single commit never ran.
    /// </summary>
    public void ThrowOnLinkNumber(int ordinal) => _crashOnLink = ordinal;

    /// <summary>Stops crashing, modelling whatever caused it having been fixed.</summary>
    public void StopCrashing() => _crashOnLink = 0;

    /// <summary>Makes the hardlink of one destination file name land nothing, so verify fails.</summary>
    public void FailVerifyFor(string targetFileName) => _failingTargets[targetFileName] = true;

    /// <summary>Clears the per-file verify failures, modelling the condition that caused them clearing.</summary>
    public void ClearFailures() => _failingTargets.Clear();

    /// <summary>Registers the files that live under a content path (and marks each as existing).</summary>
    public void SeedContent(string contentPath, params ImportFileEntry[] entries)
    {
        _contents[contentPath] = [.. entries];
        foreach (var entry in entries)
        {
            _files[entry.Path] = entry.Size;
        }
    }

    /// <summary>
    /// Every file currently on disk under a root — the library, after an import.
    /// <para>
    /// The root is canonicalised the way the path guard canonicalises it before confining a landing,
    /// and compared the way the host filesystem compares. A landed path is whatever
    /// <c>Path.GetFullPath</c> made of the configured root on this machine, which on Windows is not
    /// the string the root was configured with.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> FilesUnder(string root)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return [.. _files.Keys.Where(path => path.StartsWith(canonical, comparison)).Order()];
    }

    public bool DirectoryExists(string path) => ContentOf(path) is not null;

    public bool FileExists(string path) => _files.ContainsKey(path);

    /// <summary>Two files are the same content here when both exist with the same size.</summary>
    public Task<bool> HasSameContentAsync(string first, string second, CancellationToken cancellationToken = default) =>
        Task.FromResult(_files.TryGetValue(first, out var a) && _files.TryGetValue(second, out var b) && a == b);

    public bool RootAccessible(string root) => RootIsAccessible;

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

        return _files.TryGetValue(contentPath, out var size) ? [new ImportFileEntry(contentPath, size)] : [];
    }

    /// <summary>
    /// A stable fingerprint of the content, derived from the entry the tree was seeded with rather
    /// than from the path. Renaming a staged file therefore keeps its fingerprint, which is what makes
    /// "matched by content, not by name" a fact this fake can falsify.
    /// </summary>
    public Task<string> ComputeFingerprintAsync(string path, CancellationToken cancellationToken = default) =>
        Task.FromResult($"fp-{GetSize(path)}-{Path.GetFileName(path).Length}");

    public Task EnsureDirectoryAsync(string directory, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<FileOperationType> HardlinkOrCopyAsync(
        string fromPath,
        string toPath,
        CancellationToken cancellationToken = default)
    {
        var ordinal = Interlocked.Increment(ref _links);
        Linked.Enqueue((fromPath, toPath));

        if (_crashOnLink > 0 && ordinal >= _crashOnLink)
        {
            // Not an IOException: the import handles those by rolling the operation back and carrying
            // on, which is a different behaviour with a different test. This one has to escape the
            // whole drive.
            throw new InvalidOperationException($"The process died during hardlink {ordinal}.");
        }

        if (!_failingTargets.ContainsKey(Path.GetFileName(toPath)))
        {
            _files[toPath] = _files.TryGetValue(fromPath, out var size) ? size : 0;
        }

        // The recovery scenarios run on a working storage contract; the interrupted-drive cases above
        // are about what survives a crash, not about a degraded filesystem.
        return Task.FromResult(FileOperationType.Hardlink);
    }

    public Task DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        Deleted.Enqueue(path);
        _files.TryRemove(path, out _);
        return Task.CompletedTask;
    }

    public Task MoveAsync(string fromPath, string toPath, CancellationToken cancellationToken = default)
    {
        if (!_files.TryRemove(fromPath, out var size))
        {
            return Task.CompletedTask; // already moved by an earlier attempt of the same operation
        }

        _files[toPath] = size;
        Moved.Enqueue((fromPath, toPath));
        return Task.CompletedTask;
    }
}
