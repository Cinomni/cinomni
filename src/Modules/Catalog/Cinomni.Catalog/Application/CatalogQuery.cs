using Cinomni.Catalog.Contracts;
using Cinomni.Catalog.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cinomni.Catalog.Application;

/// <summary>
/// The flat read model over works, with no viewer: it answers for the system, so it applies no access
/// filter — event and command handlers bind this one, while an endpoint must bind
/// <see cref="ICatalogBrowse"/> instead, which a guard test enforces.
/// <para>
/// It backs nothing that renders per-caller, so it stays a single query over <c>works</c> (plus the small
/// external-id include) and must <b>never</b> <c>Include</c> seasons or episodes — that is an immediate
/// cartesian product. The series counters it exposes come from the denormalised rollup columns on
/// <c>Work</c>, which exist precisely so this query never has to join down the hierarchy.
/// </para>
/// </summary>
public sealed class CatalogQuery(CatalogDbContext dbContext) : ICatalogQuery
{
    public async Task<WorkSummary?> GetByIdAsync(WorkId id, CancellationToken cancellationToken = default)
    {
        var work = await dbContext.Works
            .Include(w => w.ExternalIdentifiers)
            .AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == id.Value, cancellationToken);

        return work is null ? null : ToSummary(work);
    }

    public async Task<WorkSummary?> FindByExternalIdAsync(
        MetadataProvider provider,
        string value,
        WorkKind? kind = null,
        CancellationToken cancellationToken = default)
    {
        var workId = await dbContext.ExternalIdentifiers
            .Where(e => e.Provider == provider && e.Value == value && (kind == null || e.Kind == kind))
            .OrderBy(e => e.Kind)
            .Select(e => (Guid?)e.WorkId)
            .FirstOrDefaultAsync(cancellationToken);

        return workId is null ? null : await GetByIdAsync(new WorkId(workId.Value), cancellationToken);
    }

    public async Task<IReadOnlyList<WorkSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        var works = await dbContext.Works
            .Include(w => w.ExternalIdentifiers)
            .AsNoTracking()
            .OrderBy(w => w.SortTitle)
            .ToListAsync(cancellationToken);

        return works.Select(ToSummary).ToList();
    }

    public async Task<IReadOnlyList<WorkSummary>> GetByIdsAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        var works = await dbContext.Works
            .Include(w => w.ExternalIdentifiers)
            .AsNoTracking()
            .Where(w => ids.Contains(w.Id))
            .ToListAsync(cancellationToken);

        return works.Select(ToSummary).ToList();
    }

    private static WorkSummary ToSummary(Work work) => CatalogBrowse.ToSummary(work);
}
