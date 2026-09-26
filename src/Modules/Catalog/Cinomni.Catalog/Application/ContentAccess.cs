using Cinomni.Catalog.Contracts;
using Cinomni.Catalog.Persistence;
using Cinomni.Kernel.Security;
using Cinomni.Metadata.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Cinomni.Catalog.Application;

/// <summary>
/// The single place the content-access predicate is written. Everything else — the catalog listing, the
/// library, subtitles, playback — composes one of these calls rather than restating the rule, so there is
/// exactly one definition of "may see" to get right and to test.
/// <para>
/// A viewer sees a collection when they administer the installation, when the collection is
/// <see cref="CollectionAccessMode.Open"/>, or when they hold a grant. Unknown works answer false: not
/// found and not yours must be indistinguishable, or a 404 becomes an oracle.
/// </para>
/// </summary>
public sealed class ContentAccess(CatalogDbContext dbContext, IContentRatingRegion region) : IContentAccess
{
    public async Task<IReadOnlyList<CollectionId>> VisibleCollectionsAsync(
        Viewer viewer,
        CancellationToken cancellationToken = default)
    {
        var ids = await VisibleCollectionIds(viewer).ToListAsync(cancellationToken);
        return ids.Select(id => new CollectionId(id)).ToList();
    }

    public Task<bool> CanSeeWorkAsync(Viewer viewer, Guid workId, CancellationToken cancellationToken = default) =>
        Visible(viewer).AnyAsync(w => w.Id == workId, cancellationToken);

    public async Task<IReadOnlySet<Guid>> FilterWorksAsync(
        Viewer viewer,
        IReadOnlyCollection<Guid> workIds,
        CancellationToken cancellationToken = default)
    {
        if (workIds.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var visible = await Visible(viewer)
            .Where(w => workIds.Contains(w.Id))
            .Select(w => w.Id)
            .ToListAsync(cancellationToken);

        return visible.ToHashSet();
    }

    /// <summary>The works this viewer may see — composed into a query, never materialised.</summary>
    internal IQueryable<Work> Visible(Viewer viewer)
    {
        if (viewer.IsAdministrator)
        {
            return dbContext.Works;
        }

        var query = dbContext.Works.Where(w => VisibleCollectionIds(viewer).Contains(w.CollectionId));
        // Certificates strictly above the ceiling. Empty means the ceiling does not apply — no region,
        // a region change, or no ceiling at all — and an unrated or unrecognised label is not in the
        // list, so it stays visible. Hiding those would hide most of a library the day the control is on.
        // Compared without case or surrounding space: the label is stored as the provider spelled it,
        // and "tv-ma" or "18 " is the same certificate as "TV-MA" or "18".
        var above = ContentRatingScale.Above(region.Current, viewer.ContentCeiling, viewer.ContentCeilingRegion)
            .Select(label => label.ToUpperInvariant())
            .ToList();
        return above.Count == 0
            ? query
            : query.Where(w => w.ContentRating == null || !above.Contains(w.ContentRating.Trim().ToUpper()));
    }

    /// <summary>The collections this viewer may browse — open to all, or granted to them.</summary>
    internal IQueryable<Guid> VisibleCollectionIds(Viewer viewer) =>
        viewer.IsAdministrator
            ? dbContext.Collections.Select(c => c.Id)
            : dbContext.Collections
                .Where(c => c.AccessMode == CollectionAccessMode.Open
                    || dbContext.Grants.Any(g => g.CollectionId == c.Id && g.UserId == viewer.UserId))
                .Select(c => c.Id);
}
