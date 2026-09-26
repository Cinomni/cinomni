using Cinomni.Kernel.Messaging;
using Cinomni.Notifications.Messaging;
using Cinomni.Operations.Messaging;
using Cinomni.Requests.Contracts;

namespace Cinomni.Notifications.EventHandlers;

/// <summary>
/// Reacts to Requests' <see cref="MediaRequested"/> by surfacing that someone is waiting for a decision.
/// The requested title is not catalogued yet, so it travels as the notification's detail rather than as a
/// work id. Enqueues a command (this handler runs in the outbox relay's transaction); idempotent by request.
/// </summary>
public sealed class MediaRequestedHandler(ICommandQueue commandQueue) : IEventHandler<MediaRequested>
{
    public async Task HandleAsync(MediaRequested domainEvent, CancellationToken cancellationToken = default)
    {
        var dedupKey = $"media-requested:{domainEvent.RequestId}";
        var year = domainEvent.Year is null ? string.Empty : $" ({domainEvent.Year})";
        var detail = $"{domainEvent.RequestedByUsername} requested {domainEvent.Title}{year}.";

        await commandQueue.EnqueueAsync(
            new RaiseNotificationCommand(NotificationKind.MediaRequested, WorkId: null, detail, dedupKey),
            idempotencyKey: $"notify:{dedupKey}",
            cancellationToken);
    }
}
