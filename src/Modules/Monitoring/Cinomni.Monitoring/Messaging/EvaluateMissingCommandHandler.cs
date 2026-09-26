using Cinomni.Catalog.Contracts;
using Cinomni.Kernel.Messaging;
using Cinomni.Kernel.Results;
using Cinomni.Monitoring.Application;
using Cinomni.Monitoring.Contracts;
using Cinomni.Monitoring.Persistence;
using Cinomni.Operations.Messaging;
using Cinomni.Operations.Settings;
using Cinomni.Operations.Transactions;
using Cinomni.Search.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cinomni.Monitoring.Messaging;

/// <summary>
/// Requests a search for every monitored, still-missing target that is due. Builds the neutral
/// <see cref="SearchCriterion"/> from the work (via Catalog's interface) so Discovery searches without
/// ever knowing the Work. The search requests and the bookkeeping (last-requested/last-evaluated) commit
/// together.
/// <para>
/// Three things make this safe at series scale, and all three are correctness rather than optimisation:
/// a <b>per-work quota</b> (a 250-episode show would otherwise consume every slot of every sweep for
/// hours), a <b>season-vs-episode granularity</b> decision (one pack request instead of twenty-two, with
/// the season's episodes stamped so they cannot ask for the same pack again), and the <b>unaired gate</b>
/// (an episode that does not exist yet is never searched, so its acquisition intent never burns an
/// attempt on it).
/// </para>
/// </summary>
public sealed class EvaluateMissingCommandHandler(
    MonitoringDbContext dbContext,
    IUnitOfWork unitOfWork,
    IEventBus eventBus,
    ICatalogQuery catalogQuery,
    ILiveOptions<MonitoringSweepOptions> sweep,
    ILogger<EvaluateMissingCommandHandler> logger)
    : ICommandHandler<EvaluateMissingCommand>
{
    public async Task<Result> HandleAsync(
        EvaluateMissingCommand command,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var workIds = await DueWorkIdsAsync(now, cancellationToken);
        if (workIds.Count == 0)
        {
            return Result.Success();
        }

        var requests = await PlanAsync(workIds, now, cancellationToken);
        if (requests.Count == 0)
        {
            return Result.Success();
        }

        // One sweep is one search occasion: every request it publishes shares this window, a redelivery
        // of any of them replays it, and the next sweep gets a different one. Bucketing by clock hour
        // instead made Discovery's execute-search key — spent for ever — cap every target at one search
        // per hour, silently, while this handler stamped them all as sent.
        var window = SearchRequested.WindowFor(now);

        await unitOfWork.ExecuteAsync(async token =>
        {
            foreach (var request in requests)
            {
                // UnitIds names the catalog content the search is for; TargetRef is the work id for a
                // movie, so the movie path publishes exactly the shape it always did plus [workId].
                await eventBus.PublishAsync(
                    new SearchRequested(
                        request.Plan.Target.Id,
                        request.Plan.Target.WorkId,
                        request.Criterion,
                        SearchReason.Missing.ToString(),
                        window,
                        UnitIds: request.Plan.UnitIds),
                    token);

                foreach (var stamped in request.Plan.Stamp)
                {
                    stamped.LastSearchRequestedAt = now;
                    stamped.LastEvaluatedAt = now;
                }
            }

            await dbContext.SaveChangesAsync(token);
        }, cancellationToken);

        return Result.Success();
    }

    /// <summary>
    /// The works holding at least one leaf that is due <em>under its own cooldown</em>, oldest first.
    /// Selecting <em>works</em> rather than targets is what makes the quota fair: the movie-era global
    /// <c>OrderBy(CreatedAt).Take(200)</c> handed every slot to whichever work happened to be catalogued
    /// first. Selecting works that then turn out not to be due is worse still — they take the slots and
    /// give nothing back.
    /// </summary>
    private Task<List<Guid>> DueWorkIdsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        // The *exact* per-target cooldown, in SQL — the SQL twin of SearchGranularityPolicy.IsDue, and it
        // has to stay its twin. Selecting on the short cooldown and leaving the real one to the planner
        // let works that were still inside their six-hour cooldown fill every slot of every sweep and
        // produce no search at all, so a library larger than MaxWorksPerSweep never drained again.
        var longCooldownLapsed = now - SearchGranularityPolicy.SearchCooldown;
        var shortCooldownLapsed = now - SearchGranularityPolicy.RecentAirCooldown;
        var justAiredFloor = now - SearchGranularityPolicy.RecentAirWindow;
        var upgradeCooldownLapsed = now - SearchGranularityPolicy.UpgradeCooldown;
        // Read at use time. Zero is today's gate (aired means AirDate <= now). A positive delay is the
        // SQL twin of SearchGranularityPolicy.HasAired for a missing target, and only for a missing one.
        var delay = sweep.Current.SearchDelay < TimeSpan.Zero ? TimeSpan.Zero : sweep.Current.SearchDelay;
        var missingAiredBefore = now - delay;

        return dbContext.MonitoredTargets
            .Where(t => t.Monitored
                // An episode with no season number is not something the planner can place, so counting it
                // as due would spend a slot on a work that produces nothing.
                && (t.Kind == TargetKind.Movie || (t.Kind == TargetKind.Episode && t.SeasonNumber != null))
                && (t.IsMissing
                    ? (t.AirDate == null || t.AirDate <= missingAiredBefore)
                        && (t.LastSearchRequestedAt == null
                            || t.LastSearchRequestedAt <= longCooldownLapsed
                            // ...and the just-aired episodes that earn the thirty-minute cooldown.
                            || (t.AirDate != null
                                && t.AirDate >= justAiredFloor
                                && t.LastSearchRequestedAt <= shortCooldownLapsed))
                    // The twin of SearchGranularityPolicy.IsUpgradeDue. A target we already hold is only
                    // due once somebody judged it worth bettering, and then on the weekly clock. The
                    // premiere delay does not apply: the file is already there.
                    : (t.AirDate == null || t.AirDate <= now)
                        && t.UpgradeWanted
                        && (t.LastSearchRequestedAt == null || t.LastSearchRequestedAt <= upgradeCooldownLapsed)))
            .GroupBy(t => t.WorkId)
            .Select(group => new { WorkId = group.Key, Oldest = group.Min(t => t.CreatedAt) })
            .OrderBy(x => x.Oldest)
            .Take(SearchGranularityPolicy.MaxWorksPerSweep)
            .Select(x => x.WorkId)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Resolves each work through Catalog's interface (reads only, outside the transaction) and plans its
    /// searches within the per-work quota and the remaining global budget.
    /// </summary>
    private async Task<List<SearchRequestPlan>> PlanAsync(
        List<Guid> workIds,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var requests = new List<SearchRequestPlan>();
        var budget = SearchGranularityPolicy.MaxSearchesPerSweep;

        foreach (var workId in workIds)
        {
            if (budget <= 0)
            {
                break;
            }

            var work = await catalogQuery.GetByIdAsync(new WorkId(workId), cancellationToken);
            if (work is null)
            {
                // A target whose work has vanished is skipped rather than searched blind — and said so.
                logger.LogWarning("Monitored work {WorkId} is unknown to the catalog; skipping its sweep.", workId);
                continue;
            }

            var family = await dbContext.MonitoredTargets
                .Where(t => t.WorkId == workId)
                .ToListAsync(cancellationToken);

            var quota = Math.Min(SearchGranularityPolicy.MaxSearchesPerWork, budget);
            var delay = sweep.Current.SearchDelay < TimeSpan.Zero ? TimeSpan.Zero : sweep.Current.SearchDelay;
            foreach (var plan in SweepPlanner.PlanForWork(family, now, quota, delay))
            {
                requests.Add(new SearchRequestPlan(plan, TargetCriterion.For(work, plan.Target)));
                budget--;
            }
        }

        return requests;
    }

    /// <summary>A planned search together with the criterion resolved for it.</summary>
    private sealed record SearchRequestPlan(PlannedSearch Plan, SearchCriterion Criterion);
}
