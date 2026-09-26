namespace Cinomni.Notifications.Application;

/// <summary>
/// How long an in-app notification stays in the inbox before it is removed with its read receipts.
/// <para>
/// The window has to comfortably exceed the platform's outbox window. A notification row is also the
/// idempotency record behind <c>ux_notifications_dedup_key</c>, so removing it frees the key: were a
/// source event still publishable at that point, its redelivery would mint a duplicate notification.
/// Six months against a fortnight leaves no realistic overlap.
/// </para>
/// </summary>
public sealed class NotificationRetentionOptions
{
    /// <summary>Minimum age of a notification before it may be removed.</summary>
    public TimeSpan NotificationRetention { get; set; } = TimeSpan.FromDays(180);

    /// <summary>How often <c>notifications.retention</c> runs.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromDays(1);

    /// <exception cref="InvalidOperationException">The configured values are unusable.</exception>
    public void Validate()
    {
        if (NotificationRetention <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"Retention:Notifications:{nameof(NotificationRetention)} must be a positive duration "
                + $"(configured: {NotificationRetention}).");
        }

        if (Interval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"Retention:Notifications:{nameof(Interval)} must be a positive duration "
                + $"(configured: {Interval}).");
        }
    }
}
