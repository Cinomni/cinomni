using Cinomni.Notifications.Contracts;

namespace Cinomni.Notifications.Delivery;

/// <summary>
/// Port to an outbound delivery channel. The production adapter POSTs to the channel's webhook over an
/// SSRF-hardened client; tests substitute a fake. A failure throws — the caller treats delivery as
/// best-effort (the in-app notification is already persisted).
/// </summary>
public interface INotificationDispatcher
{
    Task DispatchAsync(NotificationChannel channel, Notification notification, CancellationToken cancellationToken = default);
}
