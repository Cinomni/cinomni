using System.Net.Http.Json;
using Cinomni.Notifications.Contracts;

namespace Cinomni.Notifications.Delivery;

/// <summary>
/// Production <see cref="INotificationDispatcher"/> that POSTs a notification to a channel's webhook over
/// an SSRF-hardened <see cref="HttpClient"/> (configured at registration). A generic webhook receives the
/// notification's fields as JSON; a Discord channel receives Discord's embed shape (coloured by severity).
/// Not exercised by unit tests (no outbound network in dev); tests use a fake dispatcher.
/// </summary>
public sealed class WebhookNotificationDispatcher(HttpClient httpClient) : INotificationDispatcher
{
    // Discord embed colours (decimal RGB) per severity.
    private const int Green = 0x2ECC71;
    private const int Red = 0xE74C3C;
    private const int Orange = 0xF1C40F;
    private const int Blue = 0x3498DB;

    public async Task DispatchAsync(NotificationChannel channel, Notification notification, CancellationToken cancellationToken = default)
    {
        var payload = channel.Kind == NotificationChannelKind.Discord
            ? DiscordPayload(notification)
            : WebhookPayload(notification);

        using var response = await httpClient.PostAsJsonAsync(channel.Target, payload, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static object WebhookPayload(Notification notification) => new
    {
        type = notification.Type,
        severity = notification.Severity.ToString(),
        title = notification.Title,
        body = notification.Body,
        workId = notification.WorkId?.ToString(),
        createdAt = notification.CreatedAt,
    };

    private static object DiscordPayload(Notification notification) => new
    {
        embeds = new[]
        {
            new
            {
                title = notification.Title,
                description = notification.Body,
                color = ColorFor(notification.Severity),
            },
        },
    };

    private static int ColorFor(NotificationSeverity severity) => severity switch
    {
        NotificationSeverity.Success => Green,
        NotificationSeverity.Error => Red,
        NotificationSeverity.Warning => Orange,
        _ => Blue,
    };
}
