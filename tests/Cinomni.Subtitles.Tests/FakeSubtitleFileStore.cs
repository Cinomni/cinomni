using System.Collections.Concurrent;
using Cinomni.Subtitles.Contracts;
using Cinomni.Subtitles.Files;

namespace Cinomni.Subtitles.Tests;

/// <summary>
/// In-memory <see cref="ISubtitleFileStore"/> that records writes without touching disk. The record is
/// a concurrent collection because a season pack's searches genuinely run in parallel — the file write
/// happens outside the provider throttle.
/// </summary>
internal sealed class FakeSubtitleFileStore : ISubtitleFileStore
{
    public ConcurrentBag<(string VideoPath, string Language, byte[] Content)> Written { get; } = [];

    public Task<string> WriteAsync(
        string videoPath,
        string language,
        bool forced,
        bool hearingImpaired,
        SubtitleFormat format,
        byte[] content,
        CancellationToken cancellationToken = default)
    {
        Written.Add((videoPath, language, content));
        var directory = Path.GetDirectoryName(videoPath);
        var stem = Path.GetFileNameWithoutExtension(videoPath);
        return Task.FromResult(Path.Combine(directory!, $"{stem}.{language}.srt"));
    }
}
