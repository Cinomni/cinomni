using Cinomni.Subtitles.Contracts;
using Cinomni.Subtitles.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cinomni.Subtitles.Application;

/// <summary>Read model over subtitle searches, their candidates and the subtitles obtained.</summary>
public sealed class SubtitleQuery(SubtitlesDbContext dbContext) : ISubtitleQuery
{
    public async Task<IReadOnlyList<SubtitleSearchSummary>> ListForAssetAsync(Guid assetId, CancellationToken cancellationToken = default)
    {
        var searches = await dbContext.Searches
            .AsNoTracking()
            .Where(s => s.AssetId == assetId)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync(cancellationToken);
        return searches.Select(ToSummary).ToList();
    }

    public async Task<SubtitleSearchDetail?> GetAsync(SubtitleSearchId searchId, CancellationToken cancellationToken = default)
    {
        var search = await dbContext.Searches
            .AsNoTracking()
            .Include(s => s.Candidates)
            .FirstOrDefaultAsync(s => s.Id == searchId.Value, cancellationToken);
        if (search is null)
        {
            return null;
        }

        var candidates = search.Candidates
            .OrderByDescending(c => c.Score)
            .Select(c => new SubtitleCandidateSummary(c.Provider, c.Release, c.Score, c.HearingImpaired, c.Reason))
            .ToList();

        SubtitleAssetSummary? asset = null;
        if (search.SubtitleAssetId is { } id)
        {
            var entity = await dbContext.Assets.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
            if (entity is not null)
            {
                var decisions = await LatestDecisionsAsync([entity.Id], cancellationToken);
                asset = ToSummary(entity, decisions.GetValueOrDefault(entity.Id));
            }
        }

        return new SubtitleSearchDetail(ToSummary(search), candidates, asset);
    }

    public async Task<IReadOnlyList<SubtitleAssetSummary>> ListObtainedAsync(Guid assetId, CancellationToken cancellationToken = default)
    {
        var assets = await dbContext.Assets
            .AsNoTracking()
            .Where(a => a.AssetId == assetId)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync(cancellationToken);
        var decisions = await LatestDecisionsAsync(assets.Select(a => a.Id).ToList(), cancellationToken);
        return assets.Select(a => ToSummary(a, decisions.GetValueOrDefault(a.Id))).ToList();
    }

    /// <summary>The newest decision per subtitle: the history is append-only, so the last row is the answer.</summary>
    private async Task<Dictionary<Guid, SubtitleSyncDecision>> LatestDecisionsAsync(
        IReadOnlyList<Guid> subtitleIds, CancellationToken cancellationToken)
    {
        if (subtitleIds.Count == 0)
        {
            return [];
        }

        var decisions = await dbContext.SyncDecisions
            .AsNoTracking()
            .Where(d => subtitleIds.Contains(d.SubtitleAssetId))
            .ToListAsync(cancellationToken);

        // Ids are UUIDv7, so within one timestamp the later row still sorts last.
        return decisions
            .GroupBy(d => d.SubtitleAssetId)
            .ToDictionary(group => group.Key, group => group.OrderBy(d => d.CreatedAt).ThenBy(d => d.Id).Last());
    }

    private static SubtitleAssetSummary ToSummary(SubtitleAsset asset, SubtitleSyncDecision? decision) => new(
        new SubtitleAssetId(asset.Id),
        asset.AssetId,
        asset.Language,
        asset.Forced,
        asset.HearingImpaired,
        asset.Format,
        asset.Path,
        asset.Provider,
        asset.Score,
        decision is null
            ? null
            : new SubtitleSyncSummary(
                decision.Outcome, decision.OffsetMs, decision.Ratio, decision.Score, decision.Reason, asset.IsSynced, decision.CreatedAt));

    private static SubtitleSearchSummary ToSummary(SubtitleSearch search) => new(
        new SubtitleSearchId(search.Id),
        search.AssetId,
        search.Language,
        search.Forced,
        search.HearingImpaired,
        search.State,
        search.Attempts,
        search.SubtitleAssetId);
}
