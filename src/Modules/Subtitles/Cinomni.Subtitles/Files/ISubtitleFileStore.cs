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
}
