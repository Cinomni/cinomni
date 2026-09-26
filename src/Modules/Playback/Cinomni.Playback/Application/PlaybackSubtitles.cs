using Cinomni.Catalog.Contracts;
using Cinomni.Kernel.Security;
using Cinomni.Library.Contracts;
using Cinomni.Playback.Contracts;
using Cinomni.Playback.Encoding;
using Cinomni.Playback.Persistence;
using Cinomni.Subtitles.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Cinomni.Playback.Application;

/// <summary>
/// Converted subtitles, kept for a while: pulling a track out of a film reads the whole file, and a
/// viewer who switches subtitles back and forth should not pay for it twice. Bounded by size — an
/// entry costs its length — and forgotten when nobody asked for it for half an hour.
/// </summary>
public sealed class SubtitleCache : IDisposable
{
    private const long MaxChars = 64L * 1024 * 1024;
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(30);

    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = MaxChars });

    public async Task<string?> GetOrCreateAsync(string key, Func<Task<string?>> create)
    {
        if (_cache.TryGetValue(key, out string? cached))
        {
            return cached;
        }

        var value = await create();
        if (value is not null)
        {
            _cache.Set(key, value, new MemoryCacheEntryOptions { Size = Math.Max(1, value.Length), SlidingExpiration = Idle });
        }

        return value;
    }

    public void Dispose() => _cache.Dispose();
}

/// <summary>
/// Serves a session's subtitle tracks as WebVTT — the one format a browser reads. An embedded text
/// track is pulled out of the file by FFmpeg; a sidecar Subtitles fetched is read from beside the
/// video and converted here. Owner and access are checked exactly as for the stream itself; a track
/// that is a picture (PGS, VobSub), that the file does not have, or whose sidecar is gone reads as
/// missing.
/// <para>
/// A sidecar path comes from the Subtitles module, which wrote it next to the video; it is still
/// confined here to the video's own directory, refused when it is a link, and read only up to
/// <see cref="MaxSidecarBytes"/>.
/// </para>
/// </summary>
public sealed class PlaybackSubtitles(
    PlaybackDbContext dbContext,
    IContentAccess access,
    ILibraryQuery library,
    ISubtitleQuery subtitles,
    ISubtitleConverter converter,
    SubtitleCache cache)
{
    private const int MaxSidecarBytes = 8 * 1024 * 1024;

    public async Task<string?> GetWebVttAsync(
        Viewer viewer, PlaybackSessionId sessionId, int streamIndex, CancellationToken cancellationToken = default)
    {
        var session = await dbContext.Sessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == sessionId.Value, cancellationToken);
        if (session is null
            || session.UserId != viewer.UserId
            || session.WorkId is not { } workId
            || !await access.CanSeeWorkAsync(viewer, workId, cancellationToken))
        {
            return null;
        }

        var asset = await library.GetAsync(new MediaAssetId(session.AssetId), cancellationToken);
        var version = asset?.Versions.FirstOrDefault(v => v.Id.Value == session.VersionId);
        var stream = version?.Streams.FirstOrDefault(s => s.StreamIndex == streamIndex && s.Type == MediaStreamType.Subtitle);
        if (stream is null || !PlaybackTracks.CanDisplay(stream))
        {
            return null;
        }

        if (!stream.IsExternal)
        {
            return await cache.GetOrCreateAsync(
                $"embedded:{session.VersionId}:{streamIndex}",
                () => converter.ExtractToWebVttAsync(session.FullPath, streamIndex, cancellationToken));
        }

        var sidecar = (await subtitles.ListObtainedAsync(session.AssetId, cancellationToken))
            .LastOrDefault(s => s.Forced == stream.IsForced
                && string.Equals(s.Language, stream.Language, StringComparison.OrdinalIgnoreCase));
        if (sidecar is null || !IsBesideVideo(sidecar.Path, session.FullPath))
        {
            return null;
        }

        return await cache.GetOrCreateAsync(
            $"sidecar:{sidecar.Id}:{sidecar.Path}",
            () => ReadSidecarAsync(sidecar, cancellationToken));
    }

    private async Task<string?> ReadSidecarAsync(SubtitleAssetSummary sidecar, CancellationToken cancellationToken)
    {
        var info = new FileInfo(sidecar.Path);
        if (!info.Exists || info.LinkTarget is not null || info.Length is 0 or > MaxSidecarBytes)
        {
            return null;
        }

        var text = WebVtt.Decode(await File.ReadAllBytesAsync(info.FullName, cancellationToken));
        return sidecar.Format switch
        {
            SubtitleFormat.Srt => WebVtt.FromSrt(text),
            SubtitleFormat.Vtt => WebVtt.FromVtt(text),
            SubtitleFormat.Ass => await converter.AssToWebVttAsync(text, cancellationToken),
            _ => null,
        };
    }

    /// <summary>Whether a sidecar sits directly in the video's own directory — where Subtitles writes it.</summary>
    internal static bool IsBesideVideo(string sidecarPath, string videoPath)
    {
        var videoDirectory = Path.GetDirectoryName(Path.GetFullPath(videoPath));
        var sidecarDirectory = Path.GetDirectoryName(Path.GetFullPath(sidecarPath));
        return videoDirectory is not null
            && sidecarDirectory is not null
            && string.Equals(
                Path.TrimEndingDirectorySeparator(videoDirectory),
                Path.TrimEndingDirectorySeparator(sidecarDirectory),
                StringComparison.Ordinal);
    }
}
