using Cinomni.Subtitles.Contracts;

namespace Cinomni.Subtitles.Files;

/// <summary>
/// Production <see cref="ISubtitleFileStore"/>: writes the subtitle as a sidecar next to the video —
/// <c>&lt;stem&gt;.&lt;lang&gt;[.forced][.hi].&lt;ext&gt;</c> — inside the video's own directory. The provider
/// content is untrusted, so it is size-capped before it is written; the file name is composed from
/// sanitised, closed-vocabulary parts (never from provider text), and the result is proven to stay
/// inside the video directory (no traversal). A synchronized copy follows the same rules, beside the
/// original under a name no other file has, and lands by rename. Not exercised by unit tests
/// (real disk is a deployment concern); tests use an in-memory fake.
/// </summary>
public sealed class LocalSubtitleFileStore : ISubtitleFileStore
{
    private const int MaxSubtitleBytes = 8 * 1024 * 1024;

    public async Task<string> WriteAsync(
        string videoPath,
        string language,
        bool forced,
        bool hearingImpaired,
        SubtitleFormat format,
        byte[] content,
        CancellationToken cancellationToken = default)
    {
        if (content.Length == 0 || content.Length > MaxSubtitleBytes)
        {
            throw new InvalidOperationException($"Refusing to write a subtitle of {content.Length} bytes.");
        }

        var directory = Path.GetDirectoryName(videoPath)
            ?? throw new InvalidOperationException($"Video path '{videoPath}' has no directory.");
        var stem = Path.GetFileNameWithoutExtension(videoPath);
        var fileName = ComposeName(stem, language, forced, hearingImpaired, format);

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var target = Path.GetFullPath(Path.Combine(root, fileName));
        if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Refusing to write a subtitle outside the video directory.");
        }

        // A write follows a link sitting at the target and overwrites whatever it points to. Such a link
        // is never this module's, so it is refused rather than written through.
        if (new FileInfo(target).LinkTarget is not null)
        {
            throw new InvalidOperationException("Refusing to write a subtitle through a symbolic link.");
        }

        await File.WriteAllBytesAsync(target, content, cancellationToken);
        return target;
    }

    public async Task<byte[]?> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.LinkTarget is not null)
        {
            return null;
        }

        // One handle for the size check and the read, and never more than the cap read through it: a
        // file that grows between a separate check and a read-everything would otherwise be read whole.
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length == 0 || stream.Length > MaxSubtitleBytes)
        {
            return null;
        }

        var buffer = new byte[MaxSubtitleBytes + 1];
        var total = 0;
        int read;
        while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken)) > 0)
        {
            total += read;
        }

        return total is 0 or > MaxSubtitleBytes ? null : buffer[..total];
    }

    public bool Exists(string path)
    {
        var file = new FileInfo(path);
        return file.Exists && file.LinkTarget is null;
    }

    public async Task<string> WriteSyncedAsync(string originalPath, byte[] content, CancellationToken cancellationToken = default)
    {
        if (content.Length == 0 || content.Length > MaxSubtitleBytes)
        {
            throw new InvalidOperationException($"Refusing to write a synchronized subtitle of {content.Length} bytes.");
        }

        var target = ConfinedSibling(originalPath, SyncedSidecar.NameFor(originalPath, SyncedSidecar.NewToken()));

        // Written whole under a name nothing reads, then renamed to a name nothing has: the copy appears
        // complete or not at all, and the rename refuses rather than replaces if anything is there.
        var temporary = ConfinedSibling(originalPath, $".{Path.GetFileName(target)}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await stream.WriteAsync(content, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporary, target, overwrite: false);
            return target;
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public Task DeleteSyncedAsync(string syncedPath, string originalPath, CancellationToken cancellationToken = default)
    {
        var file = new FileInfo(syncedPath);
        var beside = string.Equals(
            Path.GetDirectoryName(Path.GetFullPath(syncedPath)),
            Path.GetDirectoryName(Path.GetFullPath(originalPath)),
            StringComparison.Ordinal);
        if (beside && SyncedSidecar.IsSyncedName(file.Name) && file.Exists && file.LinkTarget is null)
        {
            file.Delete();
        }

        return Task.CompletedTask;
    }

    /// <summary>A file named <paramref name="fileName"/> in the directory of <paramref name="siblingPath"/>, proven to stay there.</summary>
    private static string ConfinedSibling(string siblingPath, string fileName)
    {
        var directory = Path.GetDirectoryName(siblingPath)
            ?? throw new InvalidOperationException("The subtitle path has no directory.");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var target = Path.GetFullPath(Path.Combine(root, fileName));
        if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Refusing to write a subtitle outside the video directory.");
        }

        return target;
    }

    private static string ComposeName(string stem, string language, bool forced, bool hearingImpaired, SubtitleFormat format)
    {
        var safeLanguage = new string(language.Where(char.IsAsciiLetter).ToArray());
        if (safeLanguage.Length == 0)
        {
            safeLanguage = "und";
        }

        var suffix = (forced ? ".forced" : string.Empty) + (hearingImpaired ? ".hi" : string.Empty);
        return $"{stem}.{safeLanguage}{suffix}.{Extension(format)}";
    }

    private static string Extension(SubtitleFormat format) => format switch
    {
        SubtitleFormat.Ass => "ass",
        SubtitleFormat.Vtt => "vtt",
        _ => "srt",
    };
}
