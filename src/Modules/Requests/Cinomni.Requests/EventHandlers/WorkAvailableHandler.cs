using Cinomni.Catalog.Contracts;
using Cinomni.Kernel.Messaging;
using Cinomni.Operations.Messaging;
using Cinomni.Requests.Messaging;

namespace Cinomni.Requests.EventHandlers;

/// <summary>
/// Reacts to Catalog's <see cref="WorkAvailable"/> by closing the requests that asked for that work. Enqueues
/// a command (this handler runs inside the outbox relay's transaction); idempotent by the work.
/// </summary>
public sealed class WorkAvailableHandler(ICommandQueue commandQueue) : IEventHandler<WorkAvailable>
{
    public async Task HandleAsync(WorkAvailable domainEvent, CancellationToken cancellationToken = default) =>
        await commandQueue.EnqueueAsync(
            new CloseFulfilledRequestsCommand(domainEvent.WorkId),
            idempotencyKey: $"close-requests:{domainEvent.WorkId}",
            cancellationToken);
}
