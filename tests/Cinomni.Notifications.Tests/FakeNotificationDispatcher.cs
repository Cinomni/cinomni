using Cinomni.Notifications.Contracts;
using Cinomni.Notifications.Delivery;

namespace Cinomni.Notifications.Tests;

/// <summary>
/// In-memory <see cref="INotificationDispatcher"/> that records every delivery, so channel fan-out is
/// tested deterministically without an outbound webhook. Set <see cref="Fail"/> to simulate a channel
/// that rejects (delivery is best-effort, so the in-app notification must still land).
/// </summary>
internal sealed class FakeNotificationDispatcher : INotificationDispatcher
{
    public List<(NotificationChannel Channel, Notification Notification)> Delivered { get; } = [];

    public bool Fail { get; set; }

    public Task DispatchAsync(NotificationChannel channel, Notification notification, CancellationToken cancellationToken = default)
    {
        if (Fail)
        {
            throw new HttpRequestException("simulated channel failure");
        }

        Delivered.Add((channel, notification));
        return Task.CompletedTask;
    }
}
