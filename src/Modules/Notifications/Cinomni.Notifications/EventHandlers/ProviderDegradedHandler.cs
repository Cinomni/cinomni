using Cinomni.Kernel.Messaging;
using Cinomni.Metadata.Contracts;
using Cinomni.Notifications.Messaging;
using Cinomni.Operations.Messaging;

namespace Cinomni.Notifications.EventHandlers;

/// <summary>
/// Reacts to Metadata's <see cref="ProviderDegraded"/> by raising a provider-health notification (admin
/// signal). Enqueues a command (the handler runs in the outbox relay's transaction); idempotent by
/// work + provider + attempt.
/// </summary>
public sealed class ProviderDegradedHandler(ICommandQueue commandQueue) : IEventHandler<ProviderDegraded>
{
    public async Task HandleAsync(ProviderDegraded domainEvent, CancellationToken cancellationToken = default)
    {
        var dedupKey = $"provider-degraded:{domainEvent.WorkId}:{domainEvent.Provider}:{domainEvent.Attempt}";
        await commandQueue.EnqueueAsync(
            new RaiseNotificationCommand(NotificationKind.ProviderDegraded, domainEvent.WorkId, domainEvent.Provider, dedupKey),
            idempotencyKey: $"notify:{dedupKey}",
            cancellationToken);
    }
}
