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

    /// <summary>
    /// Writes the synchronized copy of <paramref name="originalPath"/> beside it, as
    /// <c>&lt;stem&gt;.&lt;lang&gt;[.forced][.hi].synced.&lt;ext&gt;</c> (see <see cref="SyncedSidecar"/>), and
    /// returns where it landed. Atomic: a reader sees the previous copy or the new one, never half of
    /// either. The original is never written to.
    /// </summary>
    Task<string> WriteSyncedAsync(string originalPath, byte[] content, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a synchronized copy nothing plays any more. Refuses anything not named as one, so it can
    /// never take a download or a household's own file with it.
    /// </summary>
    Task DeleteSyncedAsync(string syncedPath, CancellationToken cancellationToken = default);
}
