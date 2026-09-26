namespace Cinomni.Notifications.Contracts;

/// <summary>How prominent a notification is — drives its colour and grouping in the UI.</summary>
public enum NotificationSeverity
{
    Info = 1,
    Success = 2,
    Warning = 3,
    Error = 4,
}

/// <summary>
/// A user-facing notification: something the system did that is worth surfacing (a title became
/// available, an acquisition failed, a provider degraded). Persisted in-app; optionally fanned out to
/// configured channels. Global for the movie slice (single administrator), not per-user yet.
/// </summary>
public sealed record Notification(
    Guid Id,
    string Type,
    NotificationSeverity Severity,
    string Title,
    string Body,
    Guid? WorkId,
    bool Read,
    DateTimeOffset CreatedAt);

/// <summary>The delivery transport of an outbound channel.</summary>
public enum NotificationChannelKind
{
    /// <summary>A plain JSON POST to an arbitrary webhook URL.</summary>
    Webhook = 1,

    /// <summary>A Discord channel webhook (Discord's message JSON shape).</summary>
    Discord = 2,
}

/// <summary>A configured outbound delivery channel: where notifications are pushed beyond the in-app inbox.</summary>
public sealed record NotificationChannel(
    Guid Id,
    NotificationChannelKind Kind,
    string Name,
    string Target,
    bool Enabled);
