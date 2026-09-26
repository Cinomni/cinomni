using Cinomni.Notifications.Contracts;
using Cinomni.Notifications.Messaging;

namespace Cinomni.Notifications.Application;

/// <summary>
/// The composed, presentable form of a notification (before persistence). <paramref name="AdminOnly"/>
/// is its audience: an operator concern nobody else should read.
/// </summary>
public sealed record ComposedNotification(
    string Type,
    NotificationSeverity Severity,
    string Title,
    string Body,
    bool AdminOnly = false);

/// <summary>
/// Pure mapping from a raised event to a presentable notification: its stable type key, severity, audience
/// and the user-facing title/body. No I/O — trivially unit-tested. The work title (resolved upstream) and
/// an event-specific detail (a failure reason, a provider name) fill in the copy.
/// </summary>
public static class NotificationComposer
{
    public static ComposedNotification Compose(NotificationKind kind, string? workTitle, string? detail)
    {
        var title = workTitle ?? "a title";
        return kind switch
        {
            NotificationKind.MediaAvailable => new ComposedNotification(
                "media-available",
                NotificationSeverity.Success,
                $"{Capitalize(title)} is ready to watch",
                "The download finished and the file is now in your library."),

            NotificationKind.AcquisitionFailed => new ComposedNotification(
                "acquisition-failed",
                NotificationSeverity.Error,
                $"Couldn't acquire {title}",
                string.IsNullOrWhiteSpace(detail) ? "No acceptable release was found." : detail),

            // An operator concern: a regular account can do nothing about a degraded provider.
            NotificationKind.ProviderDegraded => new ComposedNotification(
                "provider-degraded",
                NotificationSeverity.Warning,
                $"Metadata provider {detail ?? "unknown"} is degraded",
                workTitle is null
                    ? $"{detail ?? "A provider"} has failed repeatedly and is backing off."
                    : $"{detail ?? "A provider"} failed repeatedly while refreshing {workTitle}.",
                AdminOnly: true),

            // Nothing is catalogued yet when a title is requested, so the copy comes from the detail. It
            // names who asked for what, which is nobody else's business — administrators only.
            NotificationKind.MediaRequested => new ComposedNotification(
                "media-requested",
                NotificationSeverity.Info,
                "A title is waiting for approval",
                string.IsNullOrWhiteSpace(detail) ? "Someone requested a title." : detail,
                AdminOnly: true),

            // Downloads stopping is the most alarming thing this installation does on its own, so the
            // copy says what happened, what was done about it and that nothing was lost. The detail
            // carries the observed cause, which is a vocabulary rather than a sentence — it is shown
            // as-is rather than guessed at, because guessing wrong here sends an operator to the
            // wrong subsystem. Administrators only: nobody else can act on it.
            //
            // It claims what Cinomni actually guarantees. "Transfers were paused" would be a claim
            // about the engine, and the engine is reached over a socket that can be the very thing
            // that failed; holding them is this installation's own decision, and it holds whether or
            // not the sidecar could be told about it.
            NotificationKind.TunnelEgressLost => new ComposedNotification(
                "tunnel-egress-lost",
                NotificationSeverity.Error,
                "Downloads are on hold: the VPN tunnel is not carrying their traffic",
                string.IsNullOrWhiteSpace(detail)
                    ? "Transfers are held and resume by themselves once the tunnel is back."
                    : $"Observed: {detail}. Transfers are held and resume by themselves once the tunnel is back.",
                AdminOnly: true),

            NotificationKind.TunnelEgressRestored => new ComposedNotification(
                "tunnel-egress-restored",
                NotificationSeverity.Success,
                "The VPN tunnel is carrying download traffic again",
                "Downloads held during the outage have resumed from where they stopped.",
                AdminOnly: true),

            _ => new ComposedNotification("notification", NotificationSeverity.Info, "Notification", string.Empty),
        };
    }

    private static string Capitalize(string value) =>
        value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];
}
