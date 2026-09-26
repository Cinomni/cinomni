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
            .Select(c => new SubtitleCandidateSummary(c.Provider, c.Release, c.Score, c.HearingImpaired))
            .ToList();

        SubtitleAssetSummary? asset = null;
        if (search.SubtitleAssetId is { } id)
        {
            var entity = await dbContext.Assets.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
            if (entity is not null)
            {
                asset = new SubtitleAssetSummary(
                    new SubtitleAssetId(entity.Id), entity.AssetId, entity.Language, entity.Forced,
                    entity.HearingImpaired, entity.Format, entity.Path, entity.Provider, entity.Score);
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
        return assets
            .Select(a => new SubtitleAssetSummary(
                new SubtitleAssetId(a.Id), a.AssetId, a.Language, a.Forced, a.HearingImpaired, a.Format, a.Path,
                a.Provider, a.Score))
            .ToList();
    }

    private static SubtitleSearchSummary ToSummary(SubtitleSearch search) => new(
        new SubtitleSearchId(search.Id),
        search.AssetId,
        search.Language,
        search.Forced,
        search.HearingImpaired,
        search.State,
        search.Attempts);
}
