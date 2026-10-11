using Cinomni.Subtitles.Contracts;

namespace Cinomni.Subtitles.Files;

/// <summary>
/// Production <see cref="ISubtitleFileStore"/>: writes the subtitle as a sidecar next to the video —
/// <c>&lt;stem&gt;.&lt;lang&gt;[.forced][.hi].&lt;ext&gt;</c> — inside the video's own directory. The provider
/// content is untrusted, so it is size-capped before it is written; the file name is composed from
/// sanitised, closed-vocabulary parts (never from provider text), and the result is proven to stay
/// inside the video directory (no traversal). A synchronized copy follows the same rules, beside the
/// original under a fixed <c>.synced</c> name, and lands by atomic rename. Not exercised by unit tests
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
        if (!file.Exists || file.LinkTarget is not null || file.Length == 0 || file.Length > MaxSubtitleBytes)
        {
            return null;
        }

        return await File.ReadAllBytesAsync(path, cancellationToken);
    }

    public async Task<string> WriteSyncedAsync(string originalPath, byte[] content, CancellationToken cancellationToken = default)
    {
        if (content.Length == 0 || content.Length > MaxSubtitleBytes)
        {
            throw new InvalidOperationException($"Refusing to write a synchronized subtitle of {content.Length} bytes.");
        }

        var target = ConfinedSibling(originalPath, SyncedSidecar.NameFor(originalPath));
        if (new FileInfo(target).LinkTarget is not null)
        {
            throw new InvalidOperationException("Refusing to write a subtitle through a symbolic link.");
        }

        // Written whole under a name nothing reads, then renamed over the target: a rename within one
        // directory is atomic, so a player opening the file mid-write gets the old copy or the new one.
        var temporary = ConfinedSibling(originalPath, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await stream.WriteAsync(content, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporary, target, overwrite: true);
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

    public Task DeleteSyncedAsync(string syncedPath, CancellationToken cancellationToken = default)
    {
        var file = new FileInfo(syncedPath);
        if (SyncedSidecar.IsSyncedName(file.Name) && file.Exists && file.LinkTarget is null)
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
