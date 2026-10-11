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

    /// <summary>
    /// Runs inside <see cref="WriteSyncedAsync"/> before the copy is stored: the moment a pass has
    /// finished analyzing and has not committed yet, which is where a test puts a move or a revert.
    /// </summary>
    public Func<Task>? BeforeWriteSynced { get; set; }

    public bool Exists(string path) => Files.ContainsKey(path);

    public async Task<string> WriteSyncedAsync(string originalPath, byte[] content, CancellationToken cancellationToken = default)
    {
        if (BeforeWriteSynced is { } hook)
        {
            await hook();
        }

        var path = SyncedSidecar.PathFor(originalPath, SyncedSidecar.NewToken());
        Files[path] = content;
        return path;
    }

    public Task DeleteSyncedAsync(string syncedPath, string originalPath, CancellationToken cancellationToken = default)
    {
        Files.TryRemove(syncedPath, out _);
        Deleted.Add(syncedPath);
        return Task.CompletedTask;
    }
}
