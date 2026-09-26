using Cinomni.Catalog.Contracts;
using Cinomni.Import.Messaging;
using Cinomni.Kernel.Messaging;
using Cinomni.Operations.Messaging;

namespace Cinomni.Import.EventHandlers;

/// <summary>
/// Deletes the library files of a removed work — only when the administrator asked for the files to go
/// too. Enqueues a command: deleting files is an out-of-process side effect that must be retryable, and
/// this runs inside the relay's transaction. Idempotent by <c>delete-work-files:{workId}</c>.
/// </summary>
public sealed class WorkRemovedHandler(ICommandQueue commandQueue) : IEventHandler<WorkRemoved>
{
    public async Task HandleAsync(WorkRemoved domainEvent, CancellationToken cancellationToken = default)
    {
        if (!domainEvent.DeleteFiles)
        {
            return;
        }

        await commandQueue.EnqueueAsync(
            new DeleteWorkFilesCommand(domainEvent.WorkId),
            idempotencyKey: $"delete-work-files:{domainEvent.WorkId}",
            cancellationToken);
    }
}
