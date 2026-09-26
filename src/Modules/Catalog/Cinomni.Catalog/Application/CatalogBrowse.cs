using Cinomni.Catalog.Contracts;
using Cinomni.Catalog.Persistence;
using Cinomni.Kernel.Security;
using Microsoft.EntityFrameworkCore;

namespace Cinomni.Catalog.Application;

/// <summary>
/// The catalog as one person sees it. Every query starts from <see cref="ContentAccess.Visible"/>, so a
/// work in a collection they were not granted is absent from the list, null by id, and null by external
/// id — the same answer a work that does not exist gives.
/// <para>
/// This is the twin endpoints bind. <see cref="ICatalogQuery"/> stays unscoped for event and command
/// handlers, which run for the system rather than for a person; a guard test keeps it out of endpoints.
/// </para>
/// </summary>
public sealed class CatalogBrowse(CatalogDbContext dbContext, ContentAccess access) : ICatalogBrowse
{
    /// <summary>A filter control lists a few dozen genres; past that it is a provider's taxonomy, not a menu.</summary>
    private const int MaxFacetGenres = 100;

    public async Task<IReadOnlyList<WorkSummary>> ListAsync(
        Viewer viewer,
        CollectionId? collection = null,
        CancellationToken cancellationToken = default)
    {
        var works = await Shelf(viewer, collection)
            .Include(w => w.ExternalIdentifiers)
            .AsNoTracking()
            .OrderBy(w => w.SortTitle)
            .ToListAsync(cancellationToken);

        return works.Select(ToSummary).ToList();
    }

    public async Task<WorkPage> PageAsync(
        Viewer viewer,
        WorkListQuery query,
        int offset,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var skip = Math.Max(offset, 0);
        var take = CatalogPaging.Clamp(limit);
        var matching = WorkListFilter.Apply(access.Visible(viewer), query);

        var total = await matching.CountAsync(cancellationToken);

        var works = await WorkListFilter.Order(matching, query.Sort)
            .Include(w => w.ExternalIdentifiers)
            .AsNoTracking()
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);

        return new WorkPage(works.Select(ToSummary).ToList(), total, skip, take);
    }

    public async Task<WorkFacets> FacetsAsync(
        Viewer viewer,
        CollectionId? collection,
        WorkKind? kind,
        CancellationToken cancellationToken = default)
    {
        var shelf = Shelf(viewer, collection);

        var kinds = await shelf
            .GroupBy(w => w.Kind)
            .Select(g => new { Kind = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var ofKind = kind is { } only ? shelf.Where(w => w.Kind == only) : shelf;
        var genres = await ofKind
            .SelectMany(w => w.Genres)
            .GroupBy(genre => genre)
            .Select(g => new { Genre = g.Key, Count = g.Count() })
            .OrderByDescending(g => g.Count)
            .ThenBy(g => g.Genre)
            .Take(MaxFacetGenres)
            .ToListAsync(cancellationToken);

        return new WorkFacets(
            kinds.FirstOrDefault(k => k.Kind == WorkKind.Movie)?.Count ?? 0,
            kinds.FirstOrDefault(k => k.Kind == WorkKind.Series)?.Count ?? 0,
            genres.Select(g => new GenreCount(g.Genre, g.Count)).ToList());
    }

    public async Task<WorkSummary?> GetByIdAsync(Viewer viewer, WorkId id, CancellationToken cancellationToken = default)
    {
        var work = await access.Visible(viewer)
            .Include(w => w.ExternalIdentifiers)
            .AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == id.Value, cancellationToken);

        return work is null ? null : ToSummary(work);
    }

    public async Task<WorkSummary?> FindByExternalIdAsync(
        Viewer viewer,
        MetadataProvider provider,
        string value,
        WorkKind? kind = null,
        CancellationToken cancellationToken = default)
    {
        var work = await access.Visible(viewer)
            .Include(w => w.ExternalIdentifiers)
            .AsNoTracking()
            .Where(w => kind == null || w.Kind == kind)
            .OrderBy(w => w.Kind)
            .FirstOrDefaultAsync(
                w => w.ExternalIdentifiers.Any(e => e.Provider == provider && e.Value == value),
                cancellationToken);

        return work is null ? null : ToSummary(work);
    }

    public async Task<IReadOnlyList<CollectionSummary>> CollectionsAsync(
        Viewer viewer,
        CancellationToken cancellationToken = default)
    {
        var visible = access.VisibleCollectionIds(viewer);

        var rows = await dbContext.Collections
            .Where(c => visible.Contains(c.Id))
            .AsNoTracking()
            .OrderByDescending(c => c.IsDefault)
            .ThenBy(c => c.Name)
            .Select(c => new { Collection = c, WorkCount = dbContext.Works.Count(w => w.CollectionId == c.Id) })
            .ToListAsync(cancellationToken);

        return rows.Select(row => row.Collection.ToSummary(row.WorkCount)).ToList();
    }

    private IQueryable<Work> Shelf(Viewer viewer, CollectionId? collection)
    {
        var query = access.Visible(viewer);
        return collection is { } shelf ? query.Where(w => w.CollectionId == shelf.Value) : query;
    }

    internal static WorkSummary ToSummary(Work work) => new(
        new WorkId(work.Id),
        work.Kind,
        work.Title,
        work.Year,
        work.Status,
        work.HasAsset,
        work.ExternalIdentifiers.Select(e => new ExternalId(e.Provider, e.Value)).ToList(),
        work.PosterUrl,
        work.BackdropUrl,
        work.MetadataSnapshotId,
        work.EpisodeCount,
        work.AvailableEpisodeCount,
        new CollectionId(work.CollectionId),
        work.Overview,
        work.RuntimeMinutes,
        work.Genres,
        work.AwaitingMetadata);
}
