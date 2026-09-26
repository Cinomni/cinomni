namespace Cinomni.Notifications.Persistence;

/// <summary>
/// One account's receipt for one notification. Read state lives here rather than on the notification
/// because the inbox is shared: a household member marking something read must not clear it for the
/// administrator. Absence means unread, so the table only ever grows by what people actually read, and a
/// deleted notification takes its receipts with it (the FK is intra-module, allowed).
/// </summary>
public sealed class NotificationReadRecord
{
    public Guid NotificationId { get; init; }

    /// <summary>The account that read it (inter-schema reference — Notifications does not own accounts).</summary>
    public Guid UserId { get; init; }

    public DateTimeOffset ReadAt { get; init; }

    public static NotificationReadRecord Create(Guid notificationId, Guid userId, DateTimeOffset now) => new()
    {
        NotificationId = notificationId,
        UserId = userId,
        ReadAt = now,
    };
}
