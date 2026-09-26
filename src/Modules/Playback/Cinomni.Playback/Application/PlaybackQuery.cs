using Cinomni.Catalog.Contracts;
using Cinomni.Kernel.Security;
using Cinomni.Library.Contracts;
using Cinomni.Playback.Contracts;
using Cinomni.Playback.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cinomni.Playback.Application;

/// <summary>
/// Read model over playback sessions and per-(user,asset) resume progress. "Next up" is the one query
/// that reaches outside the playback schema: the episode <em>ordering</em> belongs to Catalog and the
/// mapping from an episode to a playable file belongs to Library, both read by interface only.
/// </summary>
public sealed class PlaybackQuery(
    PlaybackDbContext dbContext,
    ICatalogQuery catalog,
    ICatalogSeriesQuery series,
    IContentAccess access,
    ILibraryQuery library) : IPlaybackQuery
{
    /// <summary>Season 0 is the specials bucket; "next up" walks the numbered seasons only.</summary>
    private const int SpecialsSeasonNumber = 0;

    /// <summary>The most "continue watching" rows one request may ask for.</summary>
    public const int MaxInProgress = 50;

    /// <summary>How many recent unfinished rows are read to fill one "continue watching" page.</summary>
    private const int InProgressScan = 200;

    public async Task<PlaybackSessionDetail?> GetSessionAsync(
        Guid userId,
        PlaybackSessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        var session = await dbContext.Sessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == sessionId.Value && s.UserId == userId, cancellationToken);
        if (session is null)
        {
            return null;
        }

        var summary = new PlaybackSessionSummary(
            new PlaybackSessionId(session.Id), session.UserId, session.AssetId, session.State, session.Method,
            session.PositionTicks, session.EndReason);
        return new PlaybackSessionDetail(
            summary,
            session.Plan.ToView(),
            new StreamSelectionView(session.AudioStreamIndex, session.SubtitleStreamIndex));
    }

    public async Task<PlaybackProgressView?> GetProgressAsync(Guid userId, Guid assetId, CancellationToken cancellationToken = default)
    {
        var progress = await dbContext.Progress
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId && p.AssetId == assetId, cancellationToken);
        return progress is null ? null : ToView(progress);
    }

    public async Task<IReadOnlyList<PlaybackProgressView>> GetProgressForAssetsAsync(
        Guid userId,
        IReadOnlyList<Guid> assetIds,
        CancellationToken cancellationToken = default)
    {
        if (assetIds.Count == 0)
        {
            return [];
        }

        var rows = await dbContext.Progress
            .AsNoTracking()
            .Where(p => p.UserId == userId && assetIds.Contains(p.AssetId))
            .ToListAsync(cancellationToken);
        return rows.Select(ToView).ToList();
    }

    public async Task<IReadOnlyList<PlaybackProgressView>> GetInProgressAsync(
        Viewer viewer,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var take = Math.Clamp(limit, 1, MaxInProgress);

        // Read past the page: rows for titles the viewer lost access to are dropped after the read, and
        // the page should still come back full when there are enough others. Bounded, so a viewer with
        // years of abandoned starts costs one capped scan, not the table.
        var rows = await dbContext.Progress
            .AsNoTracking()
            .Where(p => p.UserId == viewer.UserId && !p.Played && p.PositionTicks > 0 && p.WorkId != null)
            .OrderByDescending(p => p.UpdatedAt)
            .Take(InProgressScan)
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            return [];
        }

        var visible = await access.FilterWorksAsync(
            viewer, rows.Select(p => p.WorkId!.Value).Distinct().ToList(), cancellationToken);

        return rows
            .Where(p => visible.Contains(p.WorkId!.Value))
            .Take(take)
            .Select(ToView)
            .ToList();
    }

    public async Task<NextUpView?> GetNextUpAsync(Viewer viewer, Guid workId, CancellationToken cancellationToken = default)
    {
        // Before anything is read: the answer names episodes and their titles, so a hidden series must
        // answer as though it did not exist.
        if (!await access.CanSeeWorkAsync(viewer, workId, cancellationToken))
        {
            return null;
        }

        var userId = viewer.UserId;
        var work = await catalog.GetByIdAsync(new WorkId(workId), cancellationToken);
        if (work is null || work.Kind != WorkKind.Series)
        {
            return null; // a movie has no next episode
        }

        var episodes = await series.GetAllEpisodesAsync(new WorkId(workId), cancellationToken);
        var ordered = episodes.Where(e => e.SeasonNumber != SpecialsSeasonNumber).ToList();
        if (ordered.Count == 0)
        {
            return null;
        }

        // One round trip for the whole series, then one for the progress of the assets it found.
        var assetByUnit = await AssetByUnitAsync(ordered, cancellationToken);
        if (assetByUnit.Count == 0)
        {
            return null; // nothing playable yet
        }

        var progressByAsset = (await GetProgressForAssetsAsync(userId, [.. assetByUnit.Values], cancellationToken))
            .ToDictionary(p => p.AssetId);

        foreach (var episode in ordered)
        {
            if (!assetByUnit.TryGetValue(episode.Id.Value, out var assetId))
            {
                continue; // not in the library yet — a gap does not block the ones after it
            }

            progressByAsset.TryGetValue(assetId, out var progress);
            if (progress is { Played: true })
            {
                continue;
            }

            return new NextUpView(
                workId,
                episode.Id.Value,
                assetId,
                episode.SeasonNumber,
                episode.Number,
                episode.Title,
                progress?.PositionTicks ?? 0);
        }

        return null; // every playable episode is watched
    }

    private async Task<Dictionary<Guid, Guid>> AssetByUnitAsync(
        IReadOnlyList<EpisodeSummary> episodes,
        CancellationToken cancellationToken)
    {
        var unitIds = episodes.Select(e => e.Id.Value).ToList();
        var assets = await library.GetByUnitsAsync(unitIds, cancellationToken);

        var wanted = unitIds.ToHashSet();
        var assetByUnit = new Dictionary<Guid, Guid>();
        foreach (var asset in assets)
        {
            foreach (var unitId in asset.UnitIds ?? [])
            {
                // Newest asset first (Library orders by CreatedAt desc), so the first one wins.
                if (wanted.Contains(unitId))
                {
                    assetByUnit.TryAdd(unitId, asset.Id.Value);
                }
            }
        }

        return assetByUnit;
    }

    private static PlaybackProgressView ToView(PlaybackProgress progress) => new(
        progress.AssetId,
        progress.PositionTicks,
        progress.Played,
        progress.PlayCount,
        progress.UnitId,
        progress.DurationTicks,
        progress.WorkId,
        progress.UpdatedAt);
}
