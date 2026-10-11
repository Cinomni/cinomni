using System.Collections.Concurrent;
using Cinomni.Subtitles.Contracts;
using Cinomni.Subtitles.Files;

namespace Cinomni.Subtitles.Tests;

/// <summary>
/// In-memory <see cref="ISubtitleFileStore"/> that records writes without touching disk. The record is
/// a concurrent collection because a season pack's searches genuinely run in parallel — the file write
/// happens outside the provider throttle. <see cref="Files"/> holds what each path contains, so a sync
/// pass reads back the download it is given and a test can read the synchronized copy it wrote.
/// </summary>
internal sealed class FakeSubtitleFileStore : ISubtitleFileStore
{
    public ConcurrentBag<(string VideoPath, string Language, byte[] Content)> Written { get; } = [];

    public ConcurrentDictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

    public ConcurrentBag<string> Deleted { get; } = [];

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
        var path = Path.Combine(directory!, $"{stem}.{language}.srt");
        Files[path] = content;
        return Task.FromResult(path);
    }

    public Task<byte[]?> ReadAsync(string path, CancellationToken cancellationToken = default) =>
        Task.FromResult(Files.TryGetValue(path, out var content) ? content : null);

    public Task<string> WriteSyncedAsync(string originalPath, byte[] content, CancellationToken cancellationToken = default)
    {
        var path = SyncedSidecar.PathFor(originalPath);
        Files[path] = content;
        return Task.FromResult(path);
    }

    public Task DeleteSyncedAsync(string syncedPath, CancellationToken cancellationToken = default)
    {
        Files.TryRemove(syncedPath, out _);
        Deleted.Add(syncedPath);
        return Task.CompletedTask;
    }
}
