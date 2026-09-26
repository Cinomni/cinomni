using Cinomni.Catalog.Contracts;
using Cinomni.Kernel.Messaging;
using Cinomni.Monitoring.Messaging;
using Cinomni.Operations.Messaging;

namespace Cinomni.Monitoring.EventHandlers;

/// <summary>
/// Stops watching a work an administrator removed from the catalog. Enqueues a command for the same
/// reason as <see cref="WorkAddedHandler"/>: this runs in the relay's transaction and cannot write the
/// monitoring schema. Idempotent by <c>remove-work-targets:{workId}</c>; a work is removed once.
/// </summary>
public sealed class WorkRemovedHandler(ICommandQueue commandQueue) : IEventHandler<WorkRemoved>
{
    public Task HandleAsync(WorkRemoved domainEvent, CancellationToken cancellationToken = default) =>
        commandQueue.EnqueueAsync(
            new RemoveWorkTargetsCommand(domainEvent.WorkId, domainEvent.DeleteFiles),
            idempotencyKey: $"remove-work-targets:{domainEvent.WorkId}",
            cancellationToken);
}
