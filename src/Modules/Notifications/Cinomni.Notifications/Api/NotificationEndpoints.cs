using System.Security.Claims;
using Cinomni.Kernel.Security;
using Cinomni.Notifications.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Cinomni.Notifications.Api;

/// <summary>
/// HTTP surface of the Notifications module: browse the in-app inbox and mark read (any signed-in user,
/// who only sees the notifications meant for them and whose read state is their own), and manage outbound
/// delivery channels (administrators only — a channel target is a credential).
/// </summary>
public static class NotificationEndpoints
{
    public sealed record AddChannelRequest(NotificationChannelKind Kind, string Name, string Target);

    public sealed record SetEnabledRequest(bool Enabled);

    public static IEndpointRouteBuilder MapNotificationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/notifications").RequireAuthorization();

        group.MapGet("/", async (bool? unreadOnly, int? limit, ClaimsPrincipal principal, INotificationQuery query, CancellationToken cancellationToken) =>
        {
            if (Viewer.From(principal) is not { } reader)
            {
                return Results.Unauthorized();
            }

            var notifications = await query.ListAsync(reader, unreadOnly ?? false, limit ?? 50, cancellationToken);
            return Results.Ok(notifications.Select(ToDto));
        });

        group.MapGet("/unread-count", async (ClaimsPrincipal principal, INotificationQuery query, CancellationToken cancellationToken) =>
        {
            if (Viewer.From(principal) is not { } reader)
            {
                return Results.Unauthorized();
            }

            return Results.Ok(new { count = await query.UnreadCountAsync(reader, cancellationToken) });
        });

        group.MapPost("/{id:guid}/read", async (Guid id, ClaimsPrincipal principal, INotificationInbox inbox, CancellationToken cancellationToken) =>
        {
            if (Viewer.From(principal) is not { } reader)
            {
                return Results.Unauthorized();
            }

            await inbox.MarkReadAsync(reader, id, cancellationToken);
            return Results.NoContent();
        });

        group.MapPost("/read-all", async (ClaimsPrincipal principal, INotificationInbox inbox, CancellationToken cancellationToken) =>
        {
            if (Viewer.From(principal) is not { } reader)
            {
                return Results.Unauthorized();
            }

            await inbox.MarkAllReadAsync(reader, cancellationToken);
            return Results.NoContent();
        });

        // Channel management is operator-only, and a channel's target (a Discord webhook URL) is a
        // bearer-equivalent secret — it is masked on the way out, so no browser or console ever holds it.
        var channels = group.MapGroup("/channels").RequireAuthorization(AuthorizationPolicies.Administrator);

        channels.MapGet("/", async (INotificationChannels store, CancellationToken cancellationToken) =>
            Results.Ok((await store.ListAsync(cancellationToken)).Select(ToChannelDto)));

        channels.MapPost("/", async (AddChannelRequest request, INotificationChannels store, CancellationToken cancellationToken) =>
        {
            var result = await store.AddAsync(request.Kind, request.Name, request.Target, cancellationToken);
            return result.IsSuccess
                ? Results.Created($"/api/notifications/channels/{result.Value}", new { channelId = result.Value.ToString() })
                : Results.Json(new { error = result.Error.Code, message = result.Error.Message }, statusCode: StatusCodes.Status400BadRequest);
        });

        channels.MapPut("/{id:guid}/enabled", async (Guid id, SetEnabledRequest request, INotificationChannels store, CancellationToken cancellationToken) =>
        {
            await store.SetEnabledAsync(id, request.Enabled, cancellationToken);
            return Results.NoContent();
        });

        channels.MapDelete("/{id:guid}", async (Guid id, INotificationChannels store, CancellationToken cancellationToken) =>
        {
            await store.DeleteAsync(id, cancellationToken);
            return Results.NoContent();
        });

        return endpoints;
    }

    private static object ToDto(Notification notification) => new
    {
        id = notification.Id.ToString(),
        type = notification.Type,
        severity = notification.Severity.ToString(),
        title = notification.Title,
        body = notification.Body,
        workId = notification.WorkId?.ToString(),
        read = notification.Read,
        createdAt = notification.CreatedAt,
    };

    private static object ToChannelDto(NotificationChannel channel) => new
    {
        id = channel.Id.ToString(),
        kind = channel.Kind.ToString(),
        name = channel.Name,
        target = ChannelTargets.Mask(channel.Target),
        enabled = channel.Enabled,
    };
}
