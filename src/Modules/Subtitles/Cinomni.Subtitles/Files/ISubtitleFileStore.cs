using Cinomni.Subtitles.Contracts;

namespace Cinomni.Subtitles.Files;

/// <summary>
/// Port for writing a downloaded subtitle to disk next to its video (a sidecar file). The production
/// adapter validates the content (untrusted provider data) and confines the write to the video's own
/// directory; tests substitute an in-memory fake. Returns the path the
/// subtitle landed at.
/// </summary>
public interface ISubtitleFileStore
{
    Task<string> WriteAsync(
        string videoPath,
        string language,
        bool forced,
        bool hearingImpaired,
        SubtitleFormat format,
        byte[] content,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The bytes of a subtitle this module wrote, or null when it is gone, oversized or not a plain
    /// file. Synchronization reads the original download through this and nothing else.
    /// </summary>
    Task<byte[]?> ReadAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Whether a plain file exists at <paramref name="path"/> (not a link, not a directory).</summary>
    bool Exists(string path);

    /// <summary>
    /// Writes a synchronized copy of <paramref name="originalPath"/> beside it, under a name no other file
    /// has (<c>&lt;stem&gt;.&lt;lang&gt;[.forced][.hi].synced-&lt;token&gt;.&lt;ext&gt;</c>, see
    /// <see cref="SyncedSidecar"/>), and returns where it landed. It never replaces an existing file — not
    /// the copy being played, not anything the household keeps — and never writes to the original. The
    /// copy appears whole or not at all.
    /// </summary>
    Task<string> WriteSyncedAsync(string originalPath, byte[] content, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a synchronized copy nothing plays any more. Refuses anything not named as one of this
    /// module's copies or not sitting beside <paramref name="originalPath"/>, so it can never take a
    /// download, a household's own file, or anything elsewhere with it.
    /// </summary>
    Task DeleteSyncedAsync(string syncedPath, string originalPath, CancellationToken cancellationToken = default);
}
