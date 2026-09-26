using Cinomni.Import.Contracts;
using Cinomni.Kernel.Messaging;
using Cinomni.Notifications.Messaging;
using Cinomni.Operations.Messaging;

namespace Cinomni.Notifications.EventHandlers;

/// <summary>
/// Reacts to Import's <see cref="MediaAvailable"/> by raising a "ready to watch" notification. Enqueues a
/// command (the handler runs in the outbox relay's transaction); idempotent by the asset.
/// </summary>
public sealed class MediaAvailableHandler(ICommandQueue commandQueue) : IEventHandler<MediaAvailable>
{
    public async Task HandleAsync(MediaAvailable domainEvent, CancellationToken cancellationToken = default)
    {
        var dedupKey = $"media-available:{domainEvent.AssetId}";
        await commandQueue.EnqueueAsync(
            new RaiseNotificationCommand(NotificationKind.MediaAvailable, domainEvent.WorkId, null, dedupKey),
            idempotencyKey: $"notify:{dedupKey}",
            cancellationToken);
    }
}
