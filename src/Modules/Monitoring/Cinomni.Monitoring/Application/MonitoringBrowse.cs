using Cinomni.Catalog.Contracts;
using Cinomni.Kernel.Security;
using Cinomni.Monitoring.Contracts;

namespace Cinomni.Monitoring.Application;

/// <summary>
/// The two paginated monitoring listings as one person sees them: a target is visible when its work is,
/// exactly as Catalog decides for the library itself. Monitoring knows nothing about collections or grants
/// — it asks Catalog's content access (the single authority) and never re-implements the rule.
/// <para>
/// A concrete class rather than a published interface: only this module's endpoints bind it. The unscoped
/// <see cref="IMonitoringQuery"/> stays for event/command handlers, which have no person to answer for, and
/// for the per-work routes, which check visibility directly against <see cref="IContentAccess"/> before
/// reading it — a single-work read has no page to filter, so it needs no scan loop of its own.
/// </para>
/// <para>
/// Filtering happens <em>before</em> the page is cut, not after: cutting a page of raw rows and then
/// dropping the hidden ones would silently hand back a short page, or an empty one, while rows the caller
/// may see still wait further down the table. Since Monitoring may not join Catalog's schema to filter in
/// SQL, and the installation can hold thousands of targets, this walks the underlying table in bounded scan
/// batches — never the whole table at once — narrowing each batch through Catalog's interface and only then
/// filling the requested page. An administrator skips the round trip entirely: they see everything, exactly
/// as before this change.
/// </para>
/// <para>
/// Bounded per batch is not bounded in aggregate: a member with nothing visible would otherwise walk the
/// entire table on every request, spending one Monitoring query and one Catalog round trip per batch, freely
/// repeatable by any signed-in account. <see cref="MaxScanBatches"/> caps the total work one call may spend
/// scanning; hitting it returns whatever page has been filled so far, which may be short of the requested
/// limit. That is an acceptable answer — a full table scan on demand is not. The cap only ever applies to
/// this scoped path; an administrator's unscoped read above never enters the loop.
/// </para>
/// </summary>
public sealed class MonitoringBrowse(IMonitoringQuery query, IContentAccess access)
{
    /// <summary>
    /// How many underlying rows one scan step reads before asking Catalog which of their works are
    /// visible. Wide enough that a household's handful of restricted titles rarely costs more than one or
    /// two round trips, bounded so an installation with thousands of targets is never loaded at once.
    /// <para>
    /// Internal rather than private: it is passed through <see cref="MonitoringPaging.Clamp"/> on every
    /// call, so a test pins that it never exceeds <see cref="MonitoringPaging.MaxPageSize"/> — the static
    /// constructor below is the guard that actually enforces it at runtime.
    /// </para>
    /// </summary>
    internal const int ScanBatchSize = 200;

    /// <summary>
    /// The most scan batches one call may spend narrowing rows through Catalog before it gives up and
    /// returns whatever page it has filled. At 200 rows a batch this is 10,000 rows and at most 50 Catalog
    /// round trips per request — worse than any legitimate page, but a fixed cost regardless of how many
    /// targets the installation holds, rather than one that grows with the table. Internal so the scan-cap
    /// tests can pin the exact number of batches spent without needing ten thousand rows in a real database.
    /// </summary>
    internal const int MaxScanBatches = 50;

    /// <summary>
    /// Guards the dependency between the two constants above at type-load time rather than leaving it to be
    /// discovered in production: <see cref="ScanBatchSize"/> is clamped through
    /// <see cref="MonitoringPaging.MaxPageSize"/> on every call, so if that ceiling were ever lowered below
    /// it, every batch would come back clamped short and the scan loop would stop after the very first one
    /// — a silent truncation of every member's listing, not a crash. Throwing here turns that into a loud
    /// failure the first time this type is touched, in every environment, rather than a quiet one a test
    /// happened not to cover.
    /// </summary>
    static MonitoringBrowse()
    {
        if (ScanBatchSize > MonitoringPaging.MaxPageSize)
        {
            throw new InvalidOperationException(
                $"{nameof(MonitoringBrowse)}.{nameof(ScanBatchSize)} ({ScanBatchSize}) exceeds " +
                $"{nameof(MonitoringPaging)}.{nameof(MonitoringPaging.MaxPageSize)} " +
                $"({MonitoringPaging.MaxPageSize}); every scan batch would be clamped short and the member " +
                "browse loop would silently truncate every listing.");
        }
    }

    /// <summary>Every monitored target the viewer may see, paginated by creation order.</summary>
    public async Task<IReadOnlyList<MonitoredTargetSummary>> ListAsync(
        Viewer viewer,
        int limit,
        int offset,
        CancellationToken cancellationToken = default)
    {
        limit = MonitoringPaging.Clamp(limit);
        offset = offset < 0 ? 0 : offset;

        if (viewer.IsAdministrator)
        {
            return await query.ListAsync(limit, offset, cancellationToken);
        }

        var results = new List<MonitoredTargetSummary>(limit);
        var toSkip = offset;
        var scanOffset = 0;
        var batchesScanned = 0;

        while (results.Count < limit)
        {
            var batch = await query.ListAsync(ScanBatchSize, scanOffset, cancellationToken);
            if (batch.Count == 0)
            {
                break;
            }

            scanOffset += batch.Count;
            batchesScanned++;

            var visible = await access.FilterWorksAsync(
                viewer, batch.Select(t => t.WorkId.Value).Distinct().ToList(), cancellationToken);

            foreach (var target in batch)
            {
                if (!visible.Contains(target.WorkId.Value))
                {
                    continue;
                }

                if (toSkip > 0)
                {
                    toSkip--;
                    continue;
                }

                results.Add(target);
                if (results.Count == limit)
                {
                    break;
                }
            }

            // Fewer rows than asked for means the underlying table is exhausted; scanning further would
            // just repeat empty batches.
            if (batch.Count < ScanBatchSize)
            {
                break;
            }

            // The aggregate bound: a member with nothing visible must not be able to force a scan of the
            // whole table on every request. Whatever page has been filled so far is what this returns.
            if (batchesScanned >= MaxScanBatches)
            {
                break;
            }
        }

        return results;
    }

    /// <summary>
    /// Monitored targets still lacking an asset, narrowed to what the viewer may see. A
    /// <paramref name="workId"/> scope is a single-work read (like the per-work routes): visible or not at
    /// all, with no further per-row filtering needed since every row in that scope shares the one work.
    /// </summary>
    public async Task<IReadOnlyList<MonitoredTargetSummary>> ListMissingAsync(
        Viewer viewer,
        int limit,
        WorkId? workId,
        CancellationToken cancellationToken = default)
    {
        limit = MonitoringPaging.Clamp(limit);

        if (workId is { } scope)
        {
            var canSee = viewer.IsAdministrator || await access.CanSeeWorkAsync(viewer, scope.Value, cancellationToken);
            return canSee
                ? await query.ListMissingAsync(limit, workId: scope, cancellationToken: cancellationToken)
                : [];
        }

        if (viewer.IsAdministrator)
        {
            return await query.ListMissingAsync(limit, cancellationToken: cancellationToken);
        }

        var results = new List<MonitoredTargetSummary>(limit);
        var scanOffset = 0;
        var batchesScanned = 0;

        while (results.Count < limit)
        {
            var batch = await query.ListMissingAsync(ScanBatchSize, scanOffset, cancellationToken: cancellationToken);
            if (batch.Count == 0)
            {
                break;
            }

            scanOffset += batch.Count;
            batchesScanned++;

            var visible = await access.FilterWorksAsync(
                viewer, batch.Select(t => t.WorkId.Value).Distinct().ToList(), cancellationToken);

            foreach (var target in batch)
            {
                if (!visible.Contains(target.WorkId.Value))
                {
                    continue;
                }

                results.Add(target);
                if (results.Count == limit)
                {
                    break;
                }
            }

            if (batch.Count < ScanBatchSize)
            {
                break;
            }

            // Same aggregate bound as the unfiltered listing above.
            if (batchesScanned >= MaxScanBatches)
            {
                break;
            }
        }

        return results;
    }
}
