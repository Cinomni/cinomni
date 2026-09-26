using Cinomni.Catalog.Contracts;
using Cinomni.Monitoring.Contracts;
using Cinomni.Monitoring.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cinomni.Monitoring.Application;

/// <summary>
/// Answers "what would a search for this target ask for?" — the criterion resolved from the work plus
/// the catalog units the search is trying to acquire. The scheduled sweep computes the same thing from
/// <see cref="SweepPlanner"/>; this resolves one target on demand, for a caller that already knows which
/// target it wants and is not subject to the cadence.
/// <para>
/// It deliberately does <b>not</b> apply the cooldown, the quota or the unaired gate: those govern how
/// often the platform may ask an indexer <em>by itself</em>. A person asking for one specific target has
/// already made that judgement, and the request costs exactly one search.
/// </para>
/// </summary>
public sealed class TargetSearchPlanner(MonitoringDbContext dbContext, ICatalogQuery catalogQuery)
    : ITargetSearchPlans
{
    public async Task<TargetSearchPlan?> ResolveAsync(
        MonitoredTargetId targetId,
        CancellationToken cancellationToken = default)
    {
        var target = await dbContext.MonitoredTargets
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == targetId.Value, cancellationToken);

        // The series root is a policy node: searching it would mean "download the whole show", which is
        // exactly what the season/episode targets underneath it exist to express.
        if (target is null || !target.IsAcquirable)
        {
            return null;
        }

        var work = await catalogQuery.GetByIdAsync(new WorkId(target.WorkId), cancellationToken);
        if (work is null)
        {
            return null;
        }

        return new TargetSearchPlan(
            new MonitoredTargetId(target.Id),
            new WorkId(target.WorkId),
            target.Kind,
            TargetCriterion.For(work, target),
            await ResolveUnitsAsync(target, cancellationToken),
            TargetCriterion.LabelOf(target));
    }

    /// <summary>
    /// The catalog units the search is for. A leaf watches exactly one; a season watches the episodes it
    /// is still missing, and — when it is missing none — all of them, because that is what an upgrade of
    /// the whole season would replace. A season with no episode rows falls back to itself, which is what
    /// the acquisition spine already routes by when no unit resolves.
    /// </summary>
    private async Task<IReadOnlyList<Guid>> ResolveUnitsAsync(
        MonitoredTarget target,
        CancellationToken cancellationToken)
    {
        if (target.Kind != TargetKind.Season)
        {
            return [target.TargetRef];
        }

        var episodes = await dbContext.MonitoredTargets
            .AsNoTracking()
            .Where(t => t.ParentTargetId == target.Id && t.Kind == TargetKind.Episode && t.Monitored)
            .OrderBy(t => t.EpisodeNumber)
            .Select(t => new { t.TargetRef, t.IsMissing })
            .ToListAsync(cancellationToken);

        if (episodes.Count == 0)
        {
            return [target.TargetRef];
        }

        var missing = episodes.FindAll(e => e.IsMissing);
        var wanted = missing.Count > 0 ? missing : episodes;
        return wanted.ConvertAll(e => e.TargetRef);
    }
}
