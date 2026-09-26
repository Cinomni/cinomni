using Cinomni.Metadata.Contracts;
using Cinomni.Metadata.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cinomni.Metadata.Application;

/// <summary>
/// Read model over the metadata snapshots obtained, projected to the public contract.
/// <para>
/// <see cref="GetSnapshotAsync"/> includes the artwork candidates and <b>nothing else</b>. The season and
/// episode children are read by <see cref="GetSeriesStructureAsync"/> as their own queries: joining them
/// onto the artwork include would materialise artwork × episodes rows on a path the movie detail page
/// hits on every render.
/// </para>
/// </summary>
public sealed class MetadataQuery(MetadataDbContext dbContext) : IMetadataQuery
{
    public async Task<MetadataSnapshot?> GetSnapshotAsync(MetadataSnapshotId id, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.Snapshots
            .Include(s => s.Artwork)
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id.Value, cancellationToken);

        return entity is null ? null : ToContract(entity);
    }

    public async Task<MetadataSnapshot?> GetLatestSnapshotForWorkAsync(Guid workId, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.Snapshots
            .Include(s => s.Artwork)
            .AsNoTracking()
            .Where(s => s.WorkId == workId)
            .OrderByDescending(s => s.FetchedAt)
            .FirstOrDefaultAsync(cancellationToken);

        return entity is null ? null : ToContract(entity);
    }

    public async Task<MetadataSeriesStructure?> GetSeriesStructureAsync(MetadataSnapshotId id, CancellationToken cancellationToken = default)
    {
        var exists = await dbContext.Snapshots
            .AsNoTracking()
            .AnyAsync(s => s.Id == id.Value, cancellationToken);
        if (!exists)
        {
            return null;
        }

        var seasons = await dbContext.Seasons
            .AsNoTracking()
            .Where(s => s.SnapshotId == id.Value)
            .OrderBy(s => s.Number)
            .ToListAsync(cancellationToken);

        var episodes = await dbContext.Episodes
            .AsNoTracking()
            .Where(e => e.SnapshotId == id.Value)
            .OrderBy(e => e.SeasonNumber)
            .ThenBy(e => e.Number)
            .ToListAsync(cancellationToken);

        return new MetadataSeriesStructure(
            id,
            seasons.Select(s => s.ToContract()).ToList(),
            episodes.Select(e => e.ToContract()).ToList());
    }

    private static MetadataSnapshot ToContract(MetadataSnapshotRecord entity) => new(
        new MetadataSnapshotId(entity.Id),
        entity.WorkId,
        entity.Provider,
        entity.Kind,
        entity.ExternalId,
        entity.Title,
        entity.OriginalTitle,
        entity.Year,
        entity.Overview,
        entity.RuntimeMinutes,
        entity.OriginalLanguage,
        entity.PosterUrl,
        entity.BackdropUrl,
        entity.FetchedAt,
        entity.Artwork
            .OrderBy(a => a.Kind)
            .ThenBy(a => a.SeasonNumber ?? -1)
            .ThenBy(a => a.EpisodeNumber ?? -1)
            .ThenBy(a => a.Ordinal)
            .Select(a => a.ToContract())
            .ToList(),
        entity.SeriesStatus,
        entity.FirstAired,
        entity.LastAired,
        entity.TvdbId,
        entity.ImdbId,
        entity.TmdbId,
        entity.SeasonOrder,
        entity.Genres,
        entity.ContentRating);
}
