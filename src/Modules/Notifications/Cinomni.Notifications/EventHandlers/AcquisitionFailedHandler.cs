using Cinomni.Acquisition.Contracts;
using Cinomni.Kernel.Messaging;
using Cinomni.Notifications.Messaging;
using Cinomni.Operations.Messaging;

namespace Cinomni.Notifications.EventHandlers;

/// <summary>
/// Reacts to Acquisition's <see cref="AcquisitionFailed"/> by raising a failure notification. Enqueues a
/// command (the handler runs in the outbox relay's transaction); idempotent by intent + attempt.
/// </summary>
public sealed class AcquisitionFailedHandler(ICommandQueue commandQueue) : IEventHandler<AcquisitionFailed>
{
    public async Task HandleAsync(AcquisitionFailed domainEvent, CancellationToken cancellationToken = default)
    {
        var dedupKey = $"acquisition-failed:{domainEvent.IntentId}:{domainEvent.AttemptCount}";
        await commandQueue.EnqueueAsync(
            new RaiseNotificationCommand(NotificationKind.AcquisitionFailed, domainEvent.WorkId, domainEvent.Reason, dedupKey),
            idempotencyKey: $"notify:{dedupKey}",
            cancellationToken);
    }
}
