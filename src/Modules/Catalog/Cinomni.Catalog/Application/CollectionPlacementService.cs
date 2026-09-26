using Cinomni.Catalog.Contracts;
using Cinomni.Catalog.Messaging;
using Cinomni.Catalog.Persistence;
using Cinomni.Operations.Messaging;
using Cinomni.Operations.Transactions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Cinomni.Catalog.Application;

/// <summary>The installation's collections and rules as one read, with the rules already in evaluation order.</summary>
public sealed record RuleSet(
    IReadOnlyList<Collection> Collections,
    IReadOnlyList<StoredCollectionRule> Rules,
    IReadOnlyList<RankedRule> Ordered,
    Guid DefaultCollectionId);

/// <summary>What one sweep batch did: how many works changed collection, and where the next batch starts.</summary>
public readonly record struct SweepBatch(int Moved, Guid? Last, bool Done);

/// <summary>
/// Applies the rules to works. Three callers, one decision: a work being added is placed in memory by
/// its insert, one being enriched or unpinned is re-placed under a row lock in the same unit of work
/// (<see cref="PlaceExistingAsync"/>); a rule or order change re-places the whole library through
/// <see cref="SweepBatchAsync"/>; and a preview runs the same decision without writing anything.
/// <para>
/// The sweep is batched and recoverable, never one transaction across the library. Each batch commits
/// its own moves; the queued <see cref="SweepCollectionPlacementCommand"/> chain carries the cursor, so a
/// restart part-way resumes at the next batch rather than leaving half the library under the old rules.
/// </para>
/// </summary>
public sealed class CollectionPlacementService(
    CatalogDbContext dbContext,
    IUnitOfWork unitOfWork,
    ICommandQueue commandQueue)
{
    public const int BatchSize = 500;

    private const int MaxBatchAttempts = 3;

    public async Task<RuleSet> LoadAsync(CancellationToken cancellationToken = default)
    {
        var collections = await dbContext.Collections.AsNoTracking().ToListAsync(cancellationToken);
        var rules = await dbContext.CollectionRules.AsNoTracking().ToListAsync(cancellationToken);
        var defaultId = collections.FirstOrDefault(c => c.IsDefault)?.Id ?? DefaultCollection.Id;
        return new RuleSet(collections, rules, CollectionPlacement.Order(collections, rules), defaultId);
    }

    /// <summary>
    /// Places a work that is not yet saved — a new one being added — without saving: the caller's own
    /// insert carries it, so the work is never visible on one shelf before being placed on another.
    /// </summary>
    public async Task PlaceNewAsync(Work work, CancellationToken cancellationToken = default)
    {
        if (work.CollectionPinned)
        {
            return;
        }

        var rules = await LoadAsync(cancellationToken);
        var placement = CollectionPlacement.Place(CollectionPlacement.Facts(work), rules.Ordered, rules.DefaultCollectionId);
        work.PlaceByRule(placement.CollectionId, placement.RuleId);
    }

    /// <summary>
    /// Re-places one saved work from its current row. <b>Call it inside a unit of work.</b> It locks the
    /// row first and only then reads the work and the rules, so no concurrent write can slip between the
    /// decision and the update: a pin taken meanwhile waits for this commit and then wins, and a sweep
    /// that read the row earlier finds its compare-and-set no longer holds and comes back here. Answers
    /// whether the work changed collection.
    /// </summary>
    public async Task<bool> PlaceExistingAsync(Guid workId, CancellationToken cancellationToken)
    {
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM catalog.works WHERE id = {workId} FOR UPDATE", cancellationToken);

        var work = await Project(dbContext.Works.AsNoTracking().Where(w => w.Id == workId))
            .SingleOrDefaultAsync(cancellationToken);
        if (work is null || work.Pinned)
        {
            return false;
        }

        var rules = await LoadAsync(cancellationToken);
        var placement = CollectionPlacement.Place(work.Facts, rules.Ordered, rules.DefaultCollectionId);
        if (placement.CollectionId == work.CollectionId && placement.RuleId == work.PlacedByRuleId)
        {
            return false;
        }

        await dbContext.Works
            .Where(w => w.Id == workId && !w.CollectionPinned)
            .ExecuteUpdateAsync(set => set
                .SetProperty(w => w.CollectionId, placement.CollectionId)
                .SetProperty(w => w.PlacedByRuleId, placement.RuleId), cancellationToken);

        return placement.CollectionId != work.CollectionId;
    }

    /// <summary>
    /// Queues a full sweep, inside the caller's unit of work so it commits with the rule change that
    /// needs it. It is the recovery path: if the process stops before the inline sweep finishes, the
    /// queue finishes it.
    /// </summary>
    public Task QueueSweepAsync(Guid runId, CancellationToken cancellationToken) =>
        commandQueue.EnqueueAsync(
            new SweepCollectionPlacementCommand(runId, After: null),
            idempotencyKey: $"collection-sweep:{runId}:start",
            cancellationToken);

    /// <summary>
    /// Sweeps the whole library now, batch by batch, and answers how many works changed collection. A
    /// batch that fails because a rule it chose was deleted by a concurrent save is retried against the
    /// rules as they then stand; the queued sweep is still there behind it.
    /// </summary>
    public async Task<int> SweepAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        var moved = 0;
        Guid? after = null;
        while (true)
        {
            var batch = await SweepBatchWithRetryAsync(runId, after, cancellationToken);
            moved += batch.Moved;
            if (batch.Done)
            {
                return moved;
            }

            after = batch.Last;
        }
    }

    private async Task<SweepBatch> SweepBatchWithRetryAsync(Guid runId, Guid? after, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await SweepBatchAsync(runId, after, queueNext: false, cancellationToken);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation && attempt < MaxBatchAttempts)
            {
                // The rule this batch chose was deleted under it; the next attempt reloads the rules.
            }
        }
    }

    /// <summary>
    /// Re-places the next <see cref="BatchSize"/> works after <paramref name="after"/>, in id order, in
    /// one unit of work. Deterministic, so a batch run twice — a redelivery, or the inline sweep and the
    /// queued one overlapping — moves nothing the second time.
    /// <para>
    /// Each move is a compare-and-set on what the batch read: the collection, the rule, the metadata
    /// snapshot (the facts) and the pin. If any of them changed after the read — enrichment brought new
    /// genres, an operator pinned it — the stale decision is dropped and the work is re-placed from its
    /// current row under a lock instead, so a sweep never writes a placement worked out from facts that
    /// no longer hold.
    /// </para>
    /// </summary>
    public async Task<SweepBatch> SweepBatchAsync(
        Guid runId,
        Guid? after,
        bool queueNext,
        CancellationToken cancellationToken = default)
    {
        var rules = await LoadAsync(cancellationToken);
        var page = dbContext.Works.AsNoTracking();
        if (after is { } cursor)
        {
            page = page.Where(w => w.Id.CompareTo(cursor) > 0);
        }

        var works = await Project(page.OrderBy(w => w.Id).Take(BatchSize)).ToListAsync(cancellationToken);

        var changes = works
            .Where(w => !w.Pinned)
            .Select(w => (Work: w, Placement: CollectionPlacement.Place(w.Facts, rules.Ordered, rules.DefaultCollectionId)))
            .Where(c => c.Placement.CollectionId != c.Work.CollectionId || c.Placement.RuleId != c.Work.PlacedByRuleId)
            .ToList();

        var last = works.Count > 0 ? works[^1].Id : after;
        var done = works.Count < BatchSize;
        var moved = 0;

        if (changes.Count > 0 || (queueNext && !done))
        {
            await unitOfWork.ExecuteAsync(async token =>
            {
                moved = 0;
                foreach (var (work, placement) in changes)
                {
                    var updated = await dbContext.Works
                        .Where(w => w.Id == work.Id
                            && !w.CollectionPinned
                            && w.CollectionId == work.CollectionId
                            && w.PlacedByRuleId == work.PlacedByRuleId
                            && w.MetadataSnapshotId == work.SnapshotId)
                        .ExecuteUpdateAsync(set => set
                            .SetProperty(w => w.CollectionId, placement.CollectionId)
                            .SetProperty(w => w.PlacedByRuleId, placement.RuleId), token);

                    if (updated > 0)
                    {
                        moved += placement.CollectionId != work.CollectionId ? 1 : 0;
                    }
                    else if (await PlaceExistingAsync(work.Id, token))
                    {
                        moved++;
                    }
                }

                if (queueNext && !done)
                {
                    await commandQueue.EnqueueAsync(
                        new SweepCollectionPlacementCommand(runId, last),
                        idempotencyKey: $"collection-sweep:{runId}:{last}",
                        token);
                }
            }, cancellationToken);
        }

        return new SweepBatch(moved, last, done);
    }

    /// <summary>
    /// What <paramref name="proposed"/> would do to the library, computed with the same placement the
    /// sweep uses. <paramref name="counted"/> is the subset of rules whose matches <see cref="RulePreview.Matched"/>
    /// reports — the collection being edited, or every rule for a reorder.
    /// </summary>
    public async Task<RulePreview> PreviewAsync(
        RuleSet current,
        IReadOnlyList<RankedRule> proposed,
        IReadOnlyList<RankedRule> counted,
        CancellationToken cancellationToken = default)
    {
        var works = await Project(dbContext.Works.AsNoTracking()).ToListAsync(cancellationToken);
        var names = current.Collections.ToDictionary(c => c.Id, c => c.Name);

        var matched = 0;
        var affected = new List<RulePreviewWork>();
        foreach (var work in works)
        {
            if (CollectionPlacement.AnyMatches(work.Facts, counted))
            {
                matched++;
            }

            var target = CollectionPlacement.Place(work.Facts, proposed, current.DefaultCollectionId).CollectionId;
            if (target != work.CollectionId)
            {
                affected.Add(new RulePreviewWork(
                    new WorkId(work.Id),
                    work.Facts.Title,
                    work.Facts.Year,
                    work.Facts.Kind,
                    new CollectionId(work.CollectionId),
                    names.GetValueOrDefault(work.CollectionId, string.Empty),
                    new CollectionId(target),
                    names.GetValueOrDefault(target, string.Empty),
                    work.Pinned));
            }
        }

        return new RulePreview(
            matched,
            affected.Count(w => !w.Pinned),
            affected.Count(w => w.Pinned),
            [.. affected
                .OrderBy(w => w.Pinned)
                .ThenBy(w => w.Title, StringComparer.OrdinalIgnoreCase)
                .Take(RulePreview.MaxWorks)]);
    }

    /// <summary>The rule-relevant columns of the works, never the tracked entity. Shape the query before this.</summary>
    private static IQueryable<WorkRow> Project(IQueryable<Work> works) =>
        works
            .Select(w => new WorkRow(
                w.Id,
                w.CollectionId,
                w.CollectionPinned,
                w.PlacedByRuleId,
                w.MetadataSnapshotId,
                new RuleWorkFacts(w.Kind, w.Title, w.Year, w.RuntimeMinutes, w.OriginalLanguage, w.Genres, w.ContentRating)));

    private sealed record WorkRow(Guid Id, Guid CollectionId, bool Pinned, Guid? PlacedByRuleId, Guid? SnapshotId, RuleWorkFacts Facts);
}
