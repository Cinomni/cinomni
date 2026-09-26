using Cinomni.Catalog.Contracts;
using Cinomni.Kernel.Identifiers;
using Cinomni.Kernel.Results;
using Cinomni.Monitoring.Contracts;
using Cinomni.Monitoring.Persistence;
using Cinomni.Operations.Messaging;
using Cinomni.Operations.Transactions;
using Microsoft.EntityFrameworkCore;

namespace Cinomni.Monitoring.Application;

public sealed class MonitoringCommands(
    MonitoringDbContext dbContext,
    IUnitOfWork unitOfWork,
    IEventBus eventBus,
    ICatalogQuery catalogQuery,
    SeriesTargetMaterializer materializer)
    : IMonitoringCommands
{
    public Task<Result<MonitoredTargetId>> ApplyMonitoringPolicyAsync(
        WorkId workId,
        MonitoringMode mode,
        CancellationToken cancellationToken = default) =>
        ApplyAsync(workId, mode, initialOnly: false, cancellationToken);

    /// <summary>
    /// The policy a work starts with: written only if the work has no root yet. A root that exists —
    /// already there, or inserted by a concurrent apply between the check and this insert — is left
    /// exactly as it is, so a default arriving late never overwrites a choice somebody already made.
    /// </summary>
    public Task<Result<MonitoredTargetId>> ApplyInitialPolicyAsync(
        WorkId workId,
        MonitoringMode mode,
        CancellationToken cancellationToken = default) =>
        ApplyAsync(workId, mode, initialOnly: true, cancellationToken);

    private async Task<Result<MonitoredTargetId>> ApplyAsync(
        WorkId workId,
        MonitoringMode mode,
        bool initialOnly,
        CancellationToken cancellationToken)
    {
        // Validate at the boundary: an out-of-range enum (e.g. a bogus "mode": 99 on the wire)
        // must not be persisted as a phantom monitored state.
        if (!Enum.IsDefined(mode))
        {
            return Result<MonitoredTargetId>.Failure(
                new Error("monitoring.invalid_mode", "Unknown monitoring mode."));
        }

        // Monitoring watches known works only — resolve the Work through Catalog's interface
        // (Monitoring →i Catalog), never by reading its tables.
        var work = await catalogQuery.GetByIdAsync(workId, cancellationToken);
        if (work is null)
        {
            return Result<MonitoredTargetId>.Failure(
                new Error("monitoring.unknown_work", "No catalog work exists for the given id."));
        }

        var (root, applied) = await UpsertRootAsync(work, mode, initialOnly, cancellationToken);
        if (!applied)
        {
            return Result<MonitoredTargetId>.Success(new MonitoredTargetId(root.Id));
        }

        // A series' seasons and episodes are materialised in their own unit of work. An explicit policy
        // change re-cascades over the whole subtree — that is what the user just asked for.
        if (work.Kind == WorkKind.Series)
        {
            await materializer.MaterializeAsync(workId.Value, reapplyCascade: true, cancellationToken);
        }

        return Result<MonitoredTargetId>.Success(new MonitoredTargetId(root.Id));
    }

    public async Task<Result> SetTargetMonitoredAsync(
        MonitoredTargetId targetId,
        bool monitored,
        CancellationToken cancellationToken = default)
    {
        var target = await dbContext.MonitoredTargets
            .FirstOrDefaultAsync(t => t.Id == targetId.Value, cancellationToken);
        if (target is null)
        {
            return Result.Failure(new Error("monitoring.unknown_target", "No monitored target exists for the given id."));
        }

        var enabled = new List<MonitoredTarget>();
        await unitOfWork.ExecuteAsync(async token =>
        {
            Toggle(target, monitored, enabled);
            await dbContext.SaveChangesAsync(token);
            await PublishAsync(enabled, token);
        }, cancellationToken);

        return Result.Success();
    }

    public async Task<Result> SetSubtreeMonitoredAsync(
        MonitoredTargetId targetId,
        bool monitored,
        CancellationToken cancellationToken = default)
    {
        var target = await dbContext.MonitoredTargets
            .FirstOrDefaultAsync(t => t.Id == targetId.Value, cancellationToken);
        if (target is null)
        {
            return Result.Failure(new Error("monitoring.unknown_target", "No monitored target exists for the given id."));
        }

        // The whole work is one small tree, so loading it is cheaper and simpler than a recursive CTE.
        var family = await dbContext.MonitoredTargets
            .Where(t => t.WorkId == target.WorkId)
            .ToListAsync(cancellationToken);

        var subtree = Descend(family, target);
        var enabled = new List<MonitoredTarget>();

        await unitOfWork.ExecuteAsync(async token =>
        {
            foreach (var node in subtree)
            {
                Toggle(node, monitored, enabled);
            }

            await dbContext.SaveChangesAsync(token);
            await PublishAsync(enabled, token);
        }, cancellationToken);

        return Result.Success();
    }

    public async Task<Result> ClearTargetSearchCooldownAsync(
        MonitoredTargetId targetId,
        CancellationToken cancellationToken = default)
    {
        var target = await dbContext.MonitoredTargets
            .AsNoTracking()
            .Where(t => t.Id == targetId.Value)
            .Select(t => new { t.WorkId, t.SeasonNumber })
            .FirstOrDefaultAsync(cancellationToken);

        return target is null
            ? Result.Failure(new Error("monitoring.unknown_target", "No monitored target has this id."))
            : await ClearSearchCooldownAsync(new WorkId(target.WorkId), target.SeasonNumber, cancellationToken);
    }

    public async Task<Result> ClearSearchCooldownAsync(
        WorkId workId,
        int? seasonNumber = null,
        CancellationToken cancellationToken = default)
    {
        var query = dbContext.MonitoredTargets.Where(t => t.WorkId == workId.Value);
        if (seasonNumber is { } season)
        {
            query = query.Where(t => t.SeasonNumber == season);
        }

        var targets = await query.ToListAsync(cancellationToken);
        if (targets.Count == 0)
        {
            return Result.Failure(
                new Error("monitoring.unknown_target", "No monitored target exists for the given work."));
        }

        await unitOfWork.ExecuteAsync(async token =>
        {
            foreach (var target in targets)
            {
                target.LastSearchRequestedAt = null;
            }

            await dbContext.SaveChangesAsync(token);
        }, cancellationToken);

        return Result.Success();
    }

    /// <summary>
    /// Creates the work's root target or re-applies the policy to it, publishing <c>MonitoringEnabled</c>
    /// on the enable edge — but never for a <c>Series</c> root, which is a policy node and not something
    /// the platform can acquire a file for.
    /// </summary>
    /// <returns>The root, and whether <paramref name="mode"/> was written to it (false only when
    /// <paramref name="initialOnly"/> found a root already there).</returns>
    private async Task<(MonitoredTarget Root, bool Applied)> UpsertRootAsync(
        WorkSummary work,
        MonitoringMode mode,
        bool initialOnly,
        CancellationToken cancellationToken)
    {
        var monitored = mode != MonitoringMode.None;
        var kind = work.Kind == WorkKind.Series ? TargetKind.Series : TargetKind.Movie;
        var existing = await FindRootAsync(work.Id.Value, cancellationToken);

        if (existing is not null && initialOnly)
        {
            return (existing, false);
        }

        if (existing is null)
        {
            var target = new MonitoredTarget
            {
                Id = Uuid7.New(),
                WorkId = work.Id.Value,
                Kind = kind,
                TargetRef = work.Id.Value,
                Monitored = monitored,
                Mode = mode,
                IsMissing = !work.HasAsset,
                CreatedAt = DateTimeOffset.UtcNow,
            };

            try
            {
                // The target and its MonitoringEnabled event commit together.
                await unitOfWork.ExecuteAsync(async token =>
                {
                    dbContext.MonitoredTargets.Add(target);
                    await dbContext.SaveChangesAsync(token);
                    if (monitored && target.IsAcquirable)
                    {
                        await eventBus.PublishAsync(TargetEvents.Enabled(target), token);
                    }
                }, cancellationToken);

                return (target, true);
            }
            catch (DbUpdateException ex) when (TargetEvents.IsDuplicateTarget(ex))
            {
                // A concurrent apply won the race on ux_monitored_targets_work_ref (the API and the
                // WorkAdded-driven command can arrive together). Drop our insert and converge to the
                // idempotent update below rather than surfacing a 500.
                dbContext.Entry(target).State = EntityState.Detached;
                existing = await FindRootAsync(work.Id.Value, cancellationToken);
                if (existing is null)
                {
                    throw;
                }

                if (initialOnly)
                {
                    return (existing, false);
                }
            }
        }

        // Re-applying a policy is idempotent; MonitoringEnabled fires only on the enable edge.
        var wasMonitored = existing.Monitored;
        await unitOfWork.ExecuteAsync(async token =>
        {
            existing.Mode = mode;
            existing.Monitored = monitored;
            await dbContext.SaveChangesAsync(token);
            if (monitored && !wasMonitored && existing.IsAcquirable)
            {
                await eventBus.PublishAsync(TargetEvents.Enabled(existing), token);
            }
        }, cancellationToken);

        return (existing, true);
    }

    /// <summary>
    /// The work's root row, ordered so the answer is deterministic even if a pre-index install left two.
    /// A season or episode target is never a root, whatever its creation order.
    /// </summary>
    private Task<MonitoredTarget?> FindRootAsync(Guid workId, CancellationToken cancellationToken) =>
        dbContext.MonitoredTargets
            .Where(t => t.WorkId == workId && (t.Kind == TargetKind.Movie || t.Kind == TargetKind.Series))
            .OrderBy(t => t.CreatedAt)
            .ThenBy(t => t.Id)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// Flips a target's monitored flag <em>without writing its mode</em>. The web client calls the toggle
    /// route on every click, so persisting All/None here would destroy a user's FirstSeason or Future
    /// policy. The one exception is the enable edge of a target whose mode is None: "watch nothing" and
    /// "watch this" cannot both be true, so it is promoted to All.
    /// </summary>
    private static void Toggle(MonitoredTarget target, bool monitored, List<MonitoredTarget> enabled)
    {
        var wasMonitored = target.Monitored;
        target.Monitored = monitored;

        if (!monitored || wasMonitored)
        {
            return;
        }

        if (target.Mode == MonitoringMode.None)
        {
            target.Mode = MonitoringMode.All;
        }

        if (target.IsAcquirable)
        {
            enabled.Add(target);
        }
    }

    private async Task PublishAsync(List<MonitoredTarget> enabled, CancellationToken cancellationToken)
    {
        foreach (var target in enabled)
        {
            await eventBus.PublishAsync(TargetEvents.Enabled(target), cancellationToken);
        }
    }

    /// <summary>The target plus every descendant, walking <c>ParentTargetId</c> breadth-first.</summary>
    private static List<MonitoredTarget> Descend(List<MonitoredTarget> family, MonitoredTarget root)
    {
        var childrenByParent = family
            .Where(t => t.ParentTargetId is not null)
            .GroupBy(t => t.ParentTargetId!.Value)
            .ToDictionary(group => group.Key, group => group.ToList());

        var subtree = new List<MonitoredTarget>();
        var pending = new Queue<MonitoredTarget>();
        pending.Enqueue(root);

        while (pending.Count > 0)
        {
            var node = pending.Dequeue();
            subtree.Add(node);
            if (!childrenByParent.TryGetValue(node.Id, out var children))
            {
                continue;
            }

            foreach (var child in children)
            {
                pending.Enqueue(child);
            }
        }

        return subtree;
    }
}
