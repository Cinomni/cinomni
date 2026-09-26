using Cinomni.Kernel.Messaging;
using Cinomni.Kernel.Results;
using Cinomni.Monitoring.Application;
using Cinomni.Monitoring.Contracts;
using Cinomni.Monitoring.Persistence;
using Cinomni.Operations.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cinomni.Monitoring.Messaging;

/// <summary>
/// Clears <c>IsMissing</c> on every target watching one of the landed catalog units, then rolls the
/// change up the tree: a season stops being missing when none of its episodes is, and the series root
/// when none of its seasons is.
/// <para>
/// A <em>season</em> unit id expands to its episode targets. A season pack imported as one asset
/// satisfies all N episodes, and the acquisition goal it was attached to is the season — without the
/// expansion, N−1 episodes would keep being searched for content that is already on disk.
/// </para>
/// </summary>
public sealed class MarkUnitsSatisfiedCommandHandler(
    MonitoringDbContext dbContext,
    IUnitOfWork unitOfWork,
    ILogger<MarkUnitsSatisfiedCommandHandler> logger)
    : ICommandHandler<MarkUnitsSatisfiedCommand>
{
    public async Task<Result> HandleAsync(
        MarkUnitsSatisfiedCommand command,
        CancellationToken cancellationToken = default)
    {
        var family = await dbContext.MonitoredTargets
            .Where(t => t.WorkId == command.WorkId)
            .ToListAsync(cancellationToken);

        if (family.Count == 0)
        {
            // The import landed content for a work nobody monitors — legal, but worth a trace: it is
            // otherwise indistinguishable from the rollup silently doing nothing.
            logger.LogInformation(
                "Asset {AssetId} landed for work {WorkId}, which has no monitored targets.",
                command.AssetId,
                command.WorkId);
            return Result.Success();
        }

        var satisfied = Expand(family, command.UnitIds);
        if (satisfied.Count == 0)
        {
            logger.LogWarning(
                "Asset {AssetId} for work {WorkId} matched no monitored target; units {UnitIds} are unknown here.",
                command.AssetId,
                command.WorkId,
                string.Join(',', command.UnitIds));
            return Result.Success();
        }

        await unitOfWork.ExecuteAsync(async token =>
        {
            foreach (var target in satisfied)
            {
                target.IsMissing = false;
            }

            TargetRollup.Recompute(family);
            await dbContext.SaveChangesAsync(token);
        }, cancellationToken);

        return Result.Success();
    }

    /// <summary>
    /// The targets the landed units satisfy: every target whose <c>TargetRef</c> is one of the units, plus
    /// the episode children of any season among them.
    /// </summary>
    private static List<MonitoredTarget> Expand(List<MonitoredTarget> family, IReadOnlyList<Guid> unitIds)
    {
        var units = unitIds.ToHashSet();
        var direct = family.FindAll(t => units.Contains(t.TargetRef));
        var satisfied = new List<MonitoredTarget>(direct);

        foreach (var season in direct.Where(t => t.Kind == TargetKind.Season))
        {
            satisfied.AddRange(family.Where(t => t.ParentTargetId == season.Id));
        }

        return satisfied;
    }
}
