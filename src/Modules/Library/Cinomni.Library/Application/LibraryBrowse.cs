using Cinomni.Catalog.Contracts;
using Cinomni.Kernel.Security;
using Cinomni.Library.Contracts;
using Cinomni.Library.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cinomni.Library.Application;

/// <summary>
/// The library as one person sees it: an asset is visible when its work is. Library does not know about
/// collections and must not — it asks Catalog's <see cref="IContentAccess"/>, the single authority, and
/// filters by the answer.
/// <para>
/// A concrete class rather than a published interface: nothing outside this module consumes it, so it is
/// registered and bound by the module's own endpoints (the same shape as <c>PlaybackStreamer</c>). The
/// unscoped <see cref="ILibraryQuery"/> stays for event and command handlers, which answer for the system.
/// </para>
/// </summary>
public sealed class LibraryBrowse(LibraryDbContext dbContext, IContentAccess access)
{
    public async Task<IReadOnlyList<MediaAssetSummary>> ListAsync(
        Viewer viewer,
        Guid? workId = null,
        Guid? unitId = null,
        CancellationToken cancellationToken = default)
    {
        // The unit links are part of every summary, not just the filtered branch — the unscoped twin
        // includes them unconditionally for the same reason.
        IQueryable<MediaAsset> query = dbContext.Assets.AsNoTracking().Include(a => a.UnitLinks);

        // unitId is the narrower filter (a single episode); workId keeps its shipped meaning. Either way
        // the access question is asked about the work, since that is what a collection holds.
        if (unitId is { } unit)
        {
            query = query.Where(a => a.UnitLinks.Any(l => l.UnitId == unit));
        }
        else if (workId is { } id)
        {
            // Asking about one work: one access check answers it, and a hidden work reads as empty.
            if (!await access.CanSeeWorkAsync(viewer, id, cancellationToken))
            {
                return [];
            }

            query = query.Where(a => a.WorkId == id);
        }

        var assets = await query.OrderByDescending(a => a.CreatedAt).ToListAsync(cancellationToken);
        if (assets.Count == 0)
        {
            return [];
        }

        // Narrow the page's works in one query rather than asking per asset.
        var visibleWorks = await access.FilterWorksAsync(
            viewer, assets.Select(a => a.WorkId).Distinct().ToList(), cancellationToken);

        return assets.Where(a => visibleWorks.Contains(a.WorkId)).Select(LibraryQuery.ToSummary).ToList();
    }

    public async Task<MediaAssetDetail?> GetAsync(Viewer viewer, MediaAssetId id, CancellationToken cancellationToken = default)
    {
        var asset = await dbContext.Assets
            .AsNoTracking()
            .Include(a => a.Versions).ThenInclude(v => v.Streams)
            .Include(a => a.TargetLinks)
            .FirstOrDefaultAsync(a => a.Id == id.Value, cancellationToken);

        // Not yours reads exactly like not found.
        if (asset is null || !await access.CanSeeWorkAsync(viewer, asset.WorkId, cancellationToken))
        {
            return null;
        }

        return LibraryQuery.ToDetail(asset);
    }
}
