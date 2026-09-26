using System.Collections.Concurrent;
using Cinomni.Import.Contracts;
using Cinomni.Import.Files;

namespace Cinomni.Import.Tests;

/// <summary>
/// In-memory <see cref="IImportFileSystem"/> that scripts a content tree, so the module's
/// orchestration (match, decide, recoverable operation, verify, rollback) is tested deterministically
/// without touching disk. The real hardlink adapter is a deployment concern, exercised outside the
/// unit tests (like the libtorrent sidecar). Operations are recorded for assertions.
/// </summary>
internal sealed class FakeImportFileSystem : IImportFileSystem
{
    private readonly ConcurrentDictionary<string, long> _files = new();
    private readonly ConcurrentDictionary<string, List<ImportFileEntry>> _contents = new();
    private readonly ConcurrentDictionary<string, string> _fingerprints = new();

    public List<(string From, string To)> Linked { get; } = [];

    public List<string> Deleted { get; } = [];

    public bool RootIsAccessible { get; set; } = true;

    /// <summary>When true, a hardlink/copy does not produce the destination, so verify fails (rollback path).</summary>
    public bool FailVerify { get; set; }

    private readonly ConcurrentDictionary<string, bool> _failingTargets = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Makes the hardlink of one destination file name fail to verify, so a season pack can land
    /// three of four files and stay recoverable — the case a single global flag cannot express.
    /// </summary>
    public void FailVerifyFor(string targetFileName) => _failingTargets[targetFileName] = true;

    /// <summary>Clears the per-file verify failures, modelling the condition that caused them clearing.</summary>
    public void ClearFailures()
    {
        FailVerify = false;
        _failingTargets.Clear();
    }

    /// <summary>Registers the files that live under a content path (and marks each as existing).</summary>
    public void SeedContent(string contentPath, params ImportFileEntry[] entries)
    {
        _contents[contentPath] = [.. entries];
        foreach (var entry in entries)
        {
            _files[entry.Path] = entry.Size;
        }
    }

    public void SetFingerprint(string path, string fingerprint) => _fingerprints[path] = fingerprint;

    public bool DirectoryExists(string path) => _contents.ContainsKey(path);

    /// <summary>Paths that exist as far as the import can tell, without being files it could move.</summary>
    public Func<string, bool>? AlsoExists { get; set; }

    /// <summary>Paths whose content cannot be read, the way a file on a failing disk cannot.</summary>
    public HashSet<string> Unreadable { get; } = [];

    public bool FileExists(string path) => _files.ContainsKey(path) || AlsoExists?.Invoke(path) == true;

    public Task<bool> HasSameContentAsync(string first, string second, CancellationToken cancellationToken = default)
    {
        if (Unreadable.Contains(first) || Unreadable.Contains(second))
        {
            throw new IOException("The file could not be read.");
        }

        // In this fake a file's content is its size and its fingerprint.
        return Task.FromResult(
            _files.TryGetValue(first, out var a) && _files.TryGetValue(second, out var b) && a == b
            && Fingerprint(first) == Fingerprint(second));
    }

    public bool RootAccessible(string root) => RootIsAccessible;

    public long GetSize(string path) => _files.TryGetValue(path, out var size) ? size : 0;

    public IReadOnlyList<ImportFileEntry> EnumerateFiles(string contentPath)
    {
        if (_contents.TryGetValue(contentPath, out var entries))
        {
            return entries;
        }

        return _files.TryGetValue(contentPath, out var size)
            ? [new ImportFileEntry(contentPath, size)]
            : [];
    }

    public Task<string> ComputeFingerprintAsync(string path, CancellationToken cancellationToken = default) =>
        Task.FromResult(Fingerprint(path));

    private string Fingerprint(string path) => _fingerprints.TryGetValue(path, out var hash) ? hash : $"fp-{path}";

    /// <summary>Puts a file at a path directly — something already there before the import ran.</summary>
    public void SeedFile(string path, long size, string? fingerprint = null)
    {
        _files[path] = size;
        if (fingerprint is not null)
        {
            _fingerprints[path] = fingerprint;
        }
    }

    public Task EnsureDirectoryAsync(string directory, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <summary>
    /// Makes the adapter report a copy instead of a hardlink, which is what the real one does when the
    /// two roots are not one filesystem or are owned by two accounts (DEPLOYMENT.md, "The storage
    /// contract"). The file still lands; what changes is what happened to it.
    /// </summary>
    public bool FallsBackToCopy { get; set; }

    /// <summary>
    /// Makes the adapter report a landing it cannot explain, which is what the real one does on the
    /// resumed-import path: the file is already in place from an attempt that was interrupted before it
    /// wrote its row, and the filesystem then refuses the identity that says whether the two paths are
    /// one file. Nothing is guessed and nothing is re-linked; the outcome is "unknown".
    /// </summary>
    public bool LandingCannotBeIdentified { get; set; }

    public Task<FileOperationType> HardlinkOrCopyAsync(
        string fromPath,
        string toPath,
        CancellationToken cancellationToken = default)
    {
        // Like the real adapter: a different file already there is never replaced.
        if (_files.TryGetValue(toPath, out var existing) && _files.TryGetValue(fromPath, out var incoming) && existing != incoming)
        {
            throw new IOException("The library path is already taken by a different file.");
        }

        Linked.Add((fromPath, toPath));
        if (!FailVerify && !_failingTargets.ContainsKey(Path.GetFileName(toPath)))
        {
            _files[toPath] = _files.TryGetValue(fromPath, out var size) ? size : 0;
            // A hardlink is the same file and a copy the same bytes: either way, the same content.
            _fingerprints[toPath] = Fingerprint(fromPath);
        }

        if (LandingCannotBeIdentified)
        {
            return Task.FromResult(FileOperationType.Unknown);
        }

        return Task.FromResult(FallsBackToCopy ? FileOperationType.Copy : FileOperationType.Hardlink);
    }

    public Task DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        Deleted.Add(path);
        _files.TryRemove(path, out _);
        return Task.CompletedTask;
    }

    /// <summary>Every file set aside to make room for a replacement, as (from, to).</summary>
    public List<(string From, string To)> Moved { get; } = [];

    public Task MoveAsync(string fromPath, string toPath, CancellationToken cancellationToken = default)
    {
        if (!_files.ContainsKey(fromPath))
        {
            // Already moved by an earlier attempt of the same operation — the real adapter's no-op.
            return Task.CompletedTask;
        }

        // Like the real adapter: never over a file that is already there.
        if (_files.ContainsKey(toPath))
        {
            throw new IOException($"The destination already exists: {toPath}");
        }

        _files.TryRemove(fromPath, out var size);
        _files[toPath] = size;
        _fingerprints[toPath] = Fingerprint(fromPath);
        Moved.Add((fromPath, toPath));
        return Task.CompletedTask;
    }
}
