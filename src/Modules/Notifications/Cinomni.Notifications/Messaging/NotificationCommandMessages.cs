using Cinomni.Kernel.Messaging;

namespace Cinomni.Notifications.Messaging;

/// <summary>Stable registered names of the Notifications commands.</summary>
public static class NotificationCommandNames
{
    public const string RaiseNotification = "notifications.raise";
    public const string PurgeNotifications = "notifications.purge";
}

/// <summary>
/// Ages read and unread notifications out of the inbox with their read receipts. Parameterless — the
/// scheduler constructs it and the window is deployment configuration.
/// </summary>
public sealed record PurgeNotificationsCommand : ICommand;

/// <summary>The kind of event a notification was raised for — drives its composed message and severity.</summary>
public enum NotificationKind
{
    MediaAvailable = 1,
    AcquisitionFailed = 2,
    ProviderDegraded = 3,
    MediaRequested = 4,
    TunnelEgressLost = 5,
    TunnelEgressRestored = 6,
}

/// <summary>
/// Raises a notification — enqueued by an event handler (the outbox relay dispatches handlers in its own
/// transaction, so the write happens here in a fresh unit of work). The handler resolves the work title
/// (→i Catalog), composes the message, persists it and fans it out to enabled channels. <see cref="DedupKey"/>
/// is a stable business key (same across redeliveries of the source event) so the persist is idempotent —
/// a re-executed or recovered command is a no-op, never a duplicate notification or re-fired webhook.
/// </summary>
public sealed record RaiseNotificationCommand(NotificationKind Kind, Guid? WorkId, string? Detail, string DedupKey) : ICommand;
