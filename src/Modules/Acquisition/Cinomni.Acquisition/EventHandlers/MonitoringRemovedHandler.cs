using Cinomni.Acquisition.Messaging;
using Cinomni.Kernel.Messaging;
using Cinomni.Monitoring.Contracts;
using Cinomni.Operations.Messaging;

namespace Cinomni.Acquisition.EventHandlers;

/// <summary>
/// Ends every goal of a work whose targets Monitoring deleted because the work left the catalog. Enqueues
/// a command: this runs in the relay's transaction and cannot write the acquisition schema. Idempotent by
/// <c>cancel-work-goals:{workId}</c>; a work is removed once.
/// </summary>
public sealed class MonitoringRemovedHandler(ICommandQueue commandQueue) : IEventHandler<MonitoringRemoved>
{
    public Task HandleAsync(MonitoringRemoved domainEvent, CancellationToken cancellationToken = default) =>
        commandQueue.EnqueueAsync(
            new CancelWorkGoalsCommand(domainEvent.WorkId, domainEvent.DeleteFiles),
            idempotencyKey: $"cancel-work-goals:{domainEvent.WorkId}",
            cancellationToken);
}
