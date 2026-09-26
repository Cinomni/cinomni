using Cinomni.Import.Contracts;

namespace Cinomni.Import.Files;

/// <summary>One file discovered under a content path: its absolute path and size in bytes.</summary>
public sealed record ImportFileEntry(string Path, long Size);

/// <summary>
/// Port to the filesystem for import operations. The production adapter
/// (<see cref="LocalImportFileSystem"/>) touches real disk (hardlink with copy fallback); tests
/// substitute an in-memory fake. Keeping it behind a port lets the recoverable file-operation logic
/// (verify + rollback) be tested deterministically without a real mount, and confines every write to
/// the validated library root (the caller resolves and guards paths first — see <c>PathGuard</c>).
/// </summary>
public interface IImportFileSystem
{
    /// <summary>True when the given path is an existing directory.</summary>
    bool DirectoryExists(string path);

    /// <summary>True when the given path is an existing file.</summary>
    bool FileExists(string path);

    /// <summary>
    /// True when a storage root is present and writable. A caved-in mount must not be written to
    /// (a "mount guard": guard, don't write) — the import stays recoverable instead.
    /// </summary>
    bool RootAccessible(string root);

    /// <summary>Size of a file in bytes (used to verify an operation landed intact).</summary>
    long GetSize(string path);

    /// <summary>
    /// Enumerates the files to consider: the content path itself if it is a single file, otherwise
    /// every file beneath it (recursively). Directories and the content root are excluded.
    /// </summary>
    IReadOnlyList<ImportFileEntry> EnumerateFiles(string contentPath);

    /// <summary>
    /// Computes a content fingerprint (a hash) so matching is by content, not name (case #9). May be
    /// a partial hash for large files; the fake returns a scripted value.
    /// </summary>
    Task<string> ComputeFingerprintAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether two paths hold the same bytes: the same file (a hardlink), or two files whose whole
    /// contents are equal. A prefix fingerprint is not enough here: this is how an import tells its own
    /// earlier landing from somebody's file it would otherwise have to set aside.
    /// </summary>
    Task<bool> HasSameContentAsync(string first, string second, CancellationToken cancellationToken = default);

    /// <summary>Ensures the destination directory exists.</summary>
    Task EnsureDirectoryAsync(string directory, CancellationToken cancellationToken = default);

    /// <summary>
    /// Hardlinks <paramref name="fromPath"/> to <paramref name="toPath"/> so no bytes are duplicated
    /// and the download can keep seeding; falls back to a copy when a hardlink is impossible (a
    /// different filesystem, or an account that may not link). Idempotent: a matching destination
    /// already in place is left as is.
    /// <para>
    /// Returns the operation that was actually performed —
    /// <see cref="FileOperationType.Hardlink"/> or <see cref="FileOperationType.Copy"/> — because the
    /// job's durable record has to say which of the two the household's file got. A copy costs twice
    /// the disk and survives the download being removed; a hardlink does neither, and a record that
    /// claims a hardlink where a copy happened is a decision the installation cannot explain.
    /// </para>
    /// <para>
    /// For a destination already in place — the restart path, where an earlier attempt landed the file
    /// and never got to write its row — the answer is read from the two files themselves and is
    /// <see cref="FileOperationType.Unknown"/> when the filesystem will not supply it. An implementation
    /// must not write anything to find out: the source directory belongs to a download that is still
    /// seeding.
    /// </para>
    /// <para>
    /// It never deletes or overwrites a destination that holds something else: that is an
    /// <see cref="IOException"/>, and the file stays exactly as it was.
    /// </para>
    /// </summary>
    Task<FileOperationType> HardlinkOrCopyAsync(
        string fromPath,
        string toPath,
        CancellationToken cancellationToken = default);

    /// <summary>Removes a path (used to roll a partial operation back). Idempotent.</summary>
    Task DeleteAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves a file, creating the destination directory. Used to set a superseded file aside before its
    /// replacement lands on the same path — a move rather than a copy, so an upgrade never needs room
    /// for two copies of a film on a full disk.
    /// <para>
    /// Idempotent in the way recovery needs: a source that is already gone is a no-op, because the only
    /// way to reach that state is a previous run of this very move.
    /// </para>
    /// <para>
    /// It never overwrites: a destination that already exists is an <see cref="IOException"/>, and the
    /// caller picks another place or refuses.
    /// </para>
    /// </summary>
    Task MoveAsync(string fromPath, string toPath, CancellationToken cancellationToken = default);
}
