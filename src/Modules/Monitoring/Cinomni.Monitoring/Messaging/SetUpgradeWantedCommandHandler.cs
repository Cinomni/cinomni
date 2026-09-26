using Cinomni.Kernel.Messaging;
using Cinomni.Kernel.Results;
using Cinomni.Monitoring.Persistence;
using Cinomni.Operations.Transactions;
using Microsoft.EntityFrameworkCore;

namespace Cinomni.Monitoring.Messaging;

/// <summary>
/// Writes Decision's cutoff verdict onto the targets it concerns, so the sweep can read it without
/// asking. Idempotent by construction: it sets each flag to a value rather than toggling it, so a
/// redelivered command lands on the same state.
/// </summary>
public sealed class SetUpgradeWantedCommandHandler(
    MonitoringDbContext dbContext,
    IUnitOfWork unitOfWork)
    : ICommandHandler<SetUpgradeWantedCommand>
{
    public async Task<Result> HandleAsync(
        SetUpgradeWantedCommand command,
        CancellationToken cancellationToken = default)
    {
        var units = command.UnitsWantingUpgrade.Concat(command.UnitsSatisfied).ToHashSet();
        if (units.Count == 0)
        {
            return Result.Success();
        }

        // Scoped to the work: TargetRef is a catalog id and is not unique across the table on its own.
        var targets = await dbContext.MonitoredTargets
            .Where(t => t.WorkId == command.WorkId && units.Contains(t.TargetRef))
            .ToListAsync(cancellationToken);

        if (targets.Count == 0)
        {
            // Content landed for something nobody monitors. Legal — a manual import — and nothing to do.
            return Result.Success();
        }

        var wanting = command.UnitsWantingUpgrade.ToHashSet();
        await unitOfWork.ExecuteAsync(async token =>
        {
            foreach (var target in targets)
            {
                target.UpgradeWanted = wanting.Contains(target.TargetRef);
            }

            await dbContext.SaveChangesAsync(token);
        }, cancellationToken);

        return Result.Success();
    }
}
