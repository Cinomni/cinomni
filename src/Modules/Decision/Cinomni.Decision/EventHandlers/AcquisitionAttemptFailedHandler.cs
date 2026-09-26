using Cinomni.Acquisition.Contracts;
using Cinomni.Decision.Messaging;
using Cinomni.Kernel.Messaging;
using Cinomni.Operations.Messaging;

namespace Cinomni.Decision.EventHandlers;

/// <summary>
/// Reacts to Acquisition's <see cref="AcquisitionAttemptFailed"/> by excluding that release from that
/// goal. Enqueues a command, as an event handler must: it runs inside the relay's transaction.
/// </summary>
public sealed class AcquisitionAttemptFailedHandler(ICommandQueue commandQueue) : IEventHandler<AcquisitionAttemptFailed>
{
    public async Task HandleAsync(AcquisitionAttemptFailed domainEvent, CancellationToken cancellationToken = default) =>
        await commandQueue.EnqueueAsync(
            new ExcludeReleaseForTargetCommand(domainEvent.TargetId, domainEvent.ReleaseGuid, domainEvent.Reason),
            idempotencyKey: $"exclude-release-for-target:{domainEvent.AttemptId}",
            cancellationToken);
}
