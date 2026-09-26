using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Cinomni.Kernel.Security;
using Cinomni.RealTime.Streaming;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.RealTime.Api;

/// <summary>
/// HTTP surface of the RealTime module: one authenticated Server-Sent Events stream per client
/// (SSE for server-to-client progress, REST for user actions). Any signed-in account may
/// open it; what it then receives is decided per message by the audience the server attached to it.
/// </summary>
public static class RealtimeEndpoints
{
    /// <summary>
    /// How often a comment line is written when nothing else is. It keeps an idle stream alive through
    /// proxies that reap silent connections, and it is how the client notices a dead link.
    /// </summary>
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How long an open stream goes before its session is checked again — the longest a revoked or
    /// demoted reader keeps receiving what they were allowed when it opened.
    /// </summary>
    private static readonly TimeSpan Revalidation = TimeSpan.FromSeconds(15);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapRealtimeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/realtime").RequireAuthorization();

        group.MapGet("/stream", async (HttpContext context, RealtimeHub hub, CancellationToken cancellationToken) =>
        {
            if (Viewer.From(context.User) is not { } reader)
            {
                return Results.Unauthorized();
            }

            using var connection = hub.Connect(reader);
            if (connection is null)
            {
                return Results.Json(
                    new { error = "realtime.too_many_connections", message = "Too many live connections are open." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            await StreamAsync(context, connection, reader, cancellationToken);
            return Results.Empty;
        });

        return endpoints;
    }

    private static async Task StreamAsync(
        HttpContext context,
        RealtimeConnection connection,
        Viewer reader,
        CancellationToken cancellationToken)
    {
        var response = context.Response;
        response.Headers.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache, no-transform";
        // Reverse proxies buffer a normal response; an event stream must not be held back.
        response.Headers["X-Accel-Buffering"] = "no";
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        await response.Body.FlushAsync(cancellationToken);
        var checkedAt = Stopwatch.GetTimestamp();

        while (!cancellationToken.IsCancellationRequested)
        {
            // Never past the next check: an idle stream is woken for it, a busy one reaches it on its own.
            var untilCheck = Revalidation - Stopwatch.GetElapsedTime(checkedAt);
            var wait = untilCheck < Heartbeat ? untilCheck : Heartbeat;
            var next = await WaitForMessageAsync(connection, wait > TimeSpan.Zero ? wait : TimeSpan.FromMilliseconds(1), cancellationToken);
            if (next == Wait.Closed)
            {
                return;
            }

            // Checked on the way to every write, at most once per interval: the reader was captured
            // when the stream opened, and nothing else would end it on logout, revocation, a disabled
            // account or a role taken away — it went on receiving what that reader used to be allowed.
            if (Stopwatch.GetElapsedTime(checkedAt) >= Revalidation)
            {
                if (!await StillEntitledAsync(context, reader))
                {
                    return;
                }

                checkedAt = Stopwatch.GetTimestamp();
            }

            if (next == Wait.Idle)
            {
                // A comment line is a valid SSE frame the client ignores, and writing it is what
                // surfaces a connection the network dropped without telling either end.
                await response.WriteAsync(": ping\n\n", cancellationToken);
            }

            while (connection.Reader.TryRead(out var message))
            {
                await WriteEventAsync(response, message, cancellationToken);
            }

            await response.Body.FlushAsync(cancellationToken);
        }
    }

    private enum Wait
    {
        Message,
        Idle,
        Closed,
    }

    /// <summary>
    /// Waits for something to send, or for the heartbeat interval to pass. Closed when the stream is
    /// over — the hub completed the channel or the client went away.
    /// </summary>
    private static async Task<Wait> WaitForMessageAsync(
        RealtimeConnection connection, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idle.CancelAfter(timeout);

        try
        {
            return await connection.Reader.WaitToReadAsync(idle.Token) ? Wait.Message : Wait.Closed;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Wait.Idle;
        }
        catch (OperationCanceledException)
        {
            return Wait.Closed;
        }
    }

    /// <summary>
    /// Authenticates the request again, as if it had just arrived, and says whether it still stands for
    /// the same reader. A fresh handler in a fresh scope, because the request's own result is cached and
    /// its database context would answer from what it already loaded. A reader that changed — demoted,
    /// a new ceiling — ends the stream too: the client reconnects and is admitted as who they are now.
    /// Any failure to tell counts as no: the stream is a convenience, and the client simply reconnects.
    /// <para>
    /// It is the real sign-in check, so it also refreshes the session's last-activity time: an open tab
    /// counts as activity. Nothing expires a session on inactivity today; if that changes, this needs a
    /// check that does not touch it.
    /// </para>
    /// </summary>
    internal static async Task<bool> StillEntitledAsync(HttpContext context, Viewer reader)
    {
        try
        {
            await using var scope = context.RequestServices.CreateAsyncScope();
            var schemes = scope.ServiceProvider.GetRequiredService<IAuthenticationSchemeProvider>();
            if (await schemes.GetDefaultAuthenticateSchemeAsync() is not { } scheme)
            {
                return false;
            }

            var handler = (IAuthenticationHandler)ActivatorUtilities.CreateInstance(scope.ServiceProvider, scheme.HandlerType);
            await handler.InitializeAsync(scheme, context);
            var result = await handler.AuthenticateAsync();
            return result.Succeeded && Viewer.From(result.Principal) is { } now && now == reader;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return false;
        }
    }

    private static Task WriteEventAsync(HttpResponse response, RealtimeMessage message, CancellationToken cancellationToken)
    {
        var frame = new StringBuilder()
            .Append("data: ")
            .Append(JsonSerializer.Serialize(
                new { topic = message.Topic, payload = message.Payload }, Json))
            .Append("\n\n")
            .ToString();

        return response.WriteAsync(frame, cancellationToken);
    }
}
