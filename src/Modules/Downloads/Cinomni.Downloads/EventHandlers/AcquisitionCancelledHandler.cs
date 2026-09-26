using Cinomni.Acquisition.Contracts;
using Cinomni.Downloads.Messaging;
using Cinomni.Kernel.Messaging;
using Cinomni.Operations.Messaging;

namespace Cinomni.Downloads.EventHandlers;

/// <summary>
/// Reacts to Acquisition's <see cref="AcquisitionCancelled"/> — the goal's work left the catalog — by
/// dropping the torrents that goal claimed. Enqueues a command: removing a torrent is an out-of-process
/// side effect that must be retryable. Idempotent by <c>remove-goal-downloads:{intentId}</c>.
/// </summary>
public sealed class AcquisitionCancelledHandler(ICommandQueue commandQueue) : IEventHandler<AcquisitionCancelled>
{
    public Task HandleAsync(AcquisitionCancelled domainEvent, CancellationToken cancellationToken = default) =>
        commandQueue.EnqueueAsync(
            new RemoveGoalDownloadsCommand(domainEvent.IntentId, domainEvent.DeleteFiles),
            idempotencyKey: $"remove-goal-downloads:{domainEvent.IntentId}",
            cancellationToken);
}
