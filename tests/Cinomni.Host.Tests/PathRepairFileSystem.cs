using System.Collections.Concurrent;
using Cinomni.Import.Contracts;
using Cinomni.Import.Files;

namespace Cinomni.Host.Tests;

/// <summary>
/// An in-memory library tree: the files a repair pass may find, move, or find already moved. Only the
/// operations a repair performs are implemented; the rest of the port belongs to the import spine,
/// which these tests do not drive, and answering it with a guess would hide a caller that reached it.
/// </summary>
internal sealed class PathRepairFileSystem : IImportFileSystem
{
    private readonly ConcurrentDictionary<string, long> _files = new(StringComparer.Ordinal);

    /// <summary>Set false to model a library root that is not mounted.</summary>
    public bool RootIsAccessible { get; set; } = true;

    /// <summary>Every move the pass performed, in order.</summary>
    public List<(string From, string To)> Moved { get; } = [];

    public void Seed(string path, long size = 1_000) => _files[path] = size;

    public bool Contains(string path) => _files.ContainsKey(path);

    public bool DirectoryExists(string path) =>
        _files.Keys.Any(file => file.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.Ordinal));

    public bool FileExists(string path) => _files.ContainsKey(path);

    /// <summary>Two files are the same content here when both exist with the same size.</summary>
    public Task<bool> HasSameContentAsync(string first, string second, CancellationToken cancellationToken = default) =>
        Task.FromResult(_files.TryGetValue(first, out var a) && _files.TryGetValue(second, out var b) && a == b);

    public bool RootAccessible(string root) => RootIsAccessible;

    public long GetSize(string path) => _files.TryGetValue(path, out var size) ? size : 0;

    public IReadOnlyList<ImportFileEntry> EnumerateFiles(string contentPath)
    {
        if (_files.TryGetValue(contentPath, out var single))
        {
            return [new ImportFileEntry(contentPath, single)];
        }

        var prefix = contentPath + Path.DirectorySeparatorChar;
        return
        [
            .. _files
                .Where(file => file.Key.StartsWith(prefix, StringComparison.Ordinal))
                .OrderBy(file => file.Key, StringComparer.Ordinal)
                .Select(file => new ImportFileEntry(file.Key, file.Value)),
        ];
    }

    public Task EnsureDirectoryAsync(string directory, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task MoveAsync(string fromPath, string toPath, CancellationToken cancellationToken = default)
    {
        // A source that is already gone is a no-op, as the real adapter's is: the only way to reach
        // that state is a previous run of this very move.
        if (_files.TryRemove(fromPath, out var size))
        {
            _files[toPath] = size;
            Moved.Add((fromPath, toPath));
        }

        return Task.CompletedTask;
    }

    public Task<string> ComputeFingerprintAsync(string path, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("A repair pass does not fingerprint files.");

    public Task<FileOperationType> HardlinkOrCopyAsync(
        string fromPath,
        string toPath,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("A repair pass moves files; it never links or copies them.");

    public Task DeleteAsync(string path, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("A repair pass never deletes.");
}
