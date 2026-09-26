using Cinomni.Catalog.Contracts;
using Cinomni.Kernel.Messaging;
using Cinomni.Library.Messaging;
using Cinomni.Operations.Messaging;

namespace Cinomni.Library.EventHandlers;

/// <summary>
/// Retires the assets of a work an administrator removed from the catalog. Enqueues a command: this runs
/// in the relay's transaction and cannot write the library schema. Idempotent by
/// <c>remove-work-assets:{workId}</c>; a work is removed once. The files themselves are Import's to delete.
/// </summary>
public sealed class WorkRemovedHandler(ICommandQueue commandQueue) : IEventHandler<WorkRemoved>
{
    public Task HandleAsync(WorkRemoved domainEvent, CancellationToken cancellationToken = default) =>
        commandQueue.EnqueueAsync(
            new RemoveWorkAssetsCommand(domainEvent.WorkId),
            idempotencyKey: $"remove-work-assets:{domainEvent.WorkId}",
            cancellationToken);
}
