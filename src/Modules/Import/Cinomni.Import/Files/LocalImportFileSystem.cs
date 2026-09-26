using System.Security.Cryptography;
using Cinomni.Import.Contracts;

namespace Cinomni.Import.Files;

/// <summary>
/// Production <see cref="IImportFileSystem"/> over the local filesystem. Prefers a hardlink so the
/// imported file shares bytes with the still-seeding download (staging and library share
/// one filesystem), falling back to a copy when the platform refuses the link — and
/// reporting which of the two it did, so the job's record is the outcome and not the intent. The import
/// flow is tested through an in-memory fake, exactly like the libtorrent sidecar — real hardlink
/// behaviour is a deployment concern (shared media tree, same uid) that the Host's startup contract
/// probes once per boot — with one exception: what this adapter reports for a file that is *already* in
/// place is asserted against real disk, because that is the recovery path's whole answer. Every path
/// reaching here has already been confined to a storage root by <see cref="PathGuard"/>.
/// </summary>
public sealed class LocalImportFileSystem : IImportFileSystem
{
    // A partial content hash is enough to fingerprint by content without reading a whole 40 GB file.
    private const int FingerprintPrefixBytes = 1 * 1024 * 1024;

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public bool FileExists(string path) => File.Exists(path);

    public bool RootAccessible(string root) => Directory.Exists(root);

    public long GetSize(string path) => new FileInfo(path).Length;

    public IReadOnlyList<ImportFileEntry> EnumerateFiles(string contentPath)
    {
        if (File.Exists(contentPath))
        {
            // A single-file download that is itself a link is not content of its own.
            return new FileInfo(contentPath).LinkTarget is null
                ? [new ImportFileEntry(contentPath, new FileInfo(contentPath).Length)]
                : [];
        }

        // Not a folder that is itself a link either: the walk below would go wherever it points.
        if (!Directory.Exists(contentPath) || new DirectoryInfo(contentPath).LinkTarget is not null)
        {
            return [];
        }

        // Links are not followed, into directories or to files. A torrent can create symbolic links
        // (libtorrent honours the attribute), and one pointing at the library, the host or another
        // download would otherwise be read — and landed — as this download's content.
        return Directory
            .EnumerateFiles(contentPath, "*", NotFollowingLinks)
            .Where(path => new FileInfo(path).LinkTarget is null)
            .Select(path => new ImportFileEntry(path, new FileInfo(path).Length))
            .ToList();
    }

    /// <summary>
    /// The recursive walk <c>SearchOption.AllDirectories</c> did — hidden and system files included,
    /// an unreadable directory still an error — minus every reparse point, which is how a symbolic link
    /// presents on every platform.
    /// </summary>
    private static readonly EnumerationOptions NotFollowingLinks = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = false,
        MatchType = MatchType.Win32,
    };

    public async Task<string> ComputeFingerprintAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var length = stream.Length;
        var toRead = (int)Math.Min(length, FingerprintPrefixBytes);
        var buffer = new byte[toRead];
        var read = 0;
        while (read < toRead)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, toRead - read), cancellationToken);
            if (n == 0)
            {
                break;
            }

            read += n;
        }

        var hash = SHA256.HashData(buffer.AsSpan(0, read));
        return $"{length:x}-{Convert.ToHexString(hash)}";
    }

    public async Task<bool> HasSameContentAsync(string first, string second, CancellationToken cancellationToken = default)
    {
        if (Hardlinks.AreSameFile(first, second) == true)
        {
            return true;
        }

        if (new FileInfo(first).Length != new FileInfo(second).Length)
        {
            return false;
        }

        // Read side by side and stop at the first difference: equal files are read whole — the rare case,
        // a copy an interrupted attempt already made — and different ones almost never are.
        const int chunk = 1024 * 1024;
        await using var a = new FileStream(first, FileMode.Open, FileAccess.Read, FileShare.Read, chunk, useAsync: true);
        await using var b = new FileStream(second, FileMode.Open, FileAccess.Read, FileShare.Read, chunk, useAsync: true);
        var left = new byte[chunk];
        var right = new byte[chunk];
        while (true)
        {
            var read = await a.ReadAtLeastAsync(left, chunk, throwOnEndOfStream: false, cancellationToken);
            var other = await b.ReadAtLeastAsync(right, chunk, throwOnEndOfStream: false, cancellationToken);
            if (read != other || !left.AsSpan(0, read).SequenceEqual(right.AsSpan(0, other)))
            {
                return false;
            }

            if (read < chunk)
            {
                return true;
            }
        }
    }

    public Task EnsureDirectoryAsync(string directory, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(directory);
        return Task.CompletedTask;
    }

    public Task<FileOperationType> HardlinkOrCopyAsync(
        string fromPath,
        string toPath,
        CancellationToken cancellationToken = default)
    {
        // Idempotent: an intact destination already in place is a no-op (recoverability, case #15). The
        // caller has already established that a file there is this landing's own; the size only guards
        // against a file that changed since.
        if (File.Exists(toPath))
        {
            if (new FileInfo(toPath).Length == new FileInfo(fromPath).Length)
            {
                return Task.FromResult(WhateverPutItThere(fromPath, toPath));
            }

            // Somebody's other file, or one that appeared since the caller looked: never removed here.
            throw new IOException("The library path is already taken by a different file; it was left as it is.");
        }

        // Asked again at the last moment: the scan skipped links, but a path can be replaced by one
        // between the scan and this call, and a copy would follow it wherever it leads.
        if (new FileInfo(fromPath).LinkTarget is not null)
        {
            throw new IOException("The download file is a symbolic link; it was not imported.");
        }

        if (Hardlinks.TryLink(fromPath, toPath, out _))
        {
            return Task.FromResult(FileOperationType.Hardlink);
        }

        // Different filesystem, an account that may not link, or no hardlinks at all: copy instead of
        // failing the import — and say so, because the copy is what is on disk from here on. Never over a
        // file: one that appeared between the look and the copy is not this copy's to replace.
        File.Copy(fromPath, toPath, overwrite: false);
        return Task.FromResult(FileOperationType.Copy);
    }

    public Task DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    public Task MoveAsync(string fromPath, string toPath, CancellationToken cancellationToken = default)
    {
        // Already moved: the previous attempt of this same operation got there. Not an error to retry.
        if (!File.Exists(fromPath))
        {
            return Task.CompletedTask;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(toPath)!);

        // Never over anything: every caller moves a file the household owns into a place of its own
        // choosing, and a destination that is already there is somebody's other copy. Overwriting it is
        // how a relaunched import once destroyed the previous copy it had just filed in the bin.
        File.Move(fromPath, toPath, overwrite: false);
        return Task.CompletedTask;
    }

    /// <summary>
    /// What must have put an intact destination in place before this drive started. The operation that
    /// did it was never persisted — a drive writes its operations at the end, so an interrupted one left
    /// no row — and re-linking a landed file is exactly the bug the idempotent branch above exists to
    /// avoid.
    /// <para>
    /// So the two files are asked, and nothing else: a hardlink <em>is</em> the destination being the
    /// same file as the source, which the filesystem's own identity answers exactly, for these two files,
    /// and without writing anything. What the two directories are capable of today is a different
    /// question and a misleading answer — a mount repaired between the two attempts would turn the copy
    /// that actually happened into a recorded hardlink, on the one path where the record is all there is.
    /// </para>
    /// <para>
    /// When the platform cannot supply that identity the answer is <see cref="FileOperationType.Unknown"/>
    /// rather than the likelier of the two, because a job's trail that says the wrong thing is worse than
    /// one that says it does not know.
    /// </para>
    /// </summary>
    private static FileOperationType WhateverPutItThere(string fromPath, string toPath) =>
        Hardlinks.AreSameFile(fromPath, toPath) switch
        {
            true => FileOperationType.Hardlink,
            false => FileOperationType.Copy,
            null => FileOperationType.Unknown,
        };
}
