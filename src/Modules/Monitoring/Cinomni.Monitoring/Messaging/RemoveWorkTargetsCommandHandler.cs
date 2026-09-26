using Cinomni.Kernel.Messaging;
using Cinomni.Kernel.Results;
using Cinomni.Monitoring.Contracts;
using Cinomni.Monitoring.Persistence;
using Cinomni.Operations.Messaging;
using Cinomni.Operations.Transactions;
using Microsoft.EntityFrameworkCore;

namespace Cinomni.Monitoring.Messaging;

/// <summary>
/// Deletes every target of a removed work, so the sweep never searches for it again and no listing
/// shows it, and says so with <see cref="MonitoringRemoved"/> in the same unit of work. Idempotent: a
/// redelivery finds nothing left to delete, and the event is keyed by the work.
/// </summary>
public sealed class RemoveWorkTargetsCommandHandler(
    MonitoringDbContext dbContext, IUnitOfWork unitOfWork, IEventBus eventBus)
    : ICommandHandler<RemoveWorkTargetsCommand>
{
    public async Task<Result> HandleAsync(RemoveWorkTargetsCommand command, CancellationToken cancellationToken = default)
    {
        // One statement for the whole tree: a season and its episodes go together, so the parent links
        // between them never see a half-deleted work.
        await unitOfWork.ExecuteAsync(async token =>
        {
            await dbContext.MonitoredTargets
                .Where(t => t.WorkId == command.WorkId)
                .ExecuteDeleteAsync(token);
            await eventBus.PublishAsync(new MonitoringRemoved(command.WorkId, command.DeleteFiles), token);
        }, cancellationToken);

        return Result.Success();
    }
}
