using Cinomni.Downloads.Application;
using Cinomni.Downloads.Contracts;
using Cinomni.Downloads.Persistence;
using Cinomni.Kernel.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Cinomni.Downloads.Api;

/// <summary>
/// HTTP surface of the Downloads module: observe the download tasks and issue manual control
/// (pause/resume/remove/priorities). The automatic spine — adding a download and advancing it — is
/// driven by <c>DownloadQueued</c> and the sidecar status stream, not by API.
/// </summary>
public static class DownloadEndpoints
{
    public static IEndpointRouteBuilder MapDownloadEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/downloads").RequireAuthorization(AuthorizationPolicies.Administrator);

        group.MapGet("/", async (IDownloadQuery query, CancellationToken cancellationToken) =>
            Results.Ok((await query.ListActiveAsync(cancellationToken)).Select(ToSummaryDto)));

        group.MapGet("/{id:guid}", async (Guid id, IDownloadQuery query, CancellationToken cancellationToken) =>
        {
            var detail = await query.GetAsync(new DownloadTaskId(id), cancellationToken);
            return detail is null ? Results.NotFound() : Results.Ok(ToDetailDto(detail));
        });

        group.MapPost("/{id:guid}/pause", async (Guid id, DownloadService service, CancellationToken cancellationToken) =>
        {
            try
            {
                return await service.PauseAsync(new DownloadTaskId(id), cancellationToken)
                    ? Results.NoContent()
                    : Results.NotFound();
            }
            catch (DownloadStateConflictException exception)
            {
                return InvalidState(exception);
            }
        });

        // `force` is the deliberate override the PauseAndAlert policy allows: it resumes a download
        // whose egress is not verified, records that decision on the task's history, and is refused
        // outright under Block. Absent or false, a held task answers 409 rather than 500 — the refusal
        // is an answer, not a fault.
        group.MapPost("/{id:guid}/resume",
            async (Guid id, bool? force, DownloadService service, CancellationToken cancellationToken) =>
            {
                try
                {
                    return await service.ResumeAsync(new DownloadTaskId(id), force ?? false, cancellationToken)
                        ? Results.NoContent()
                        : Results.NotFound();
                }
                catch (NetworkHoldException exception)
                {
                    return Results.Conflict(new
                    {
                        error = "downloads.network_held",
                        message =
                            "This download is held because torrent traffic is not verifiably leaving through "
                            + $"the tunnel ({exception.Reason}). It resumes on its own once the tunnel is back.",
                    });
                }
                catch (DownloadStateConflictException exception)
                {
                    return InvalidState(exception);
                }
            });

        // The operator's readiness surface for torrent egress. Deliberately here and not on the
        // anonymous /health: that probe answers whether the process is alive, and whether this
        // installation uses a tunnel — and whether it is currently leaking — is not something an
        // unauthenticated caller gets to ask.
        group.MapGet("/tunnel", async (IDownloadQuery query, CancellationToken cancellationToken) =>
        {
            var status = await query.GetTunnelStatusAsync(cancellationToken);
            return Results.Ok(new
            {
                configured = status.Configured,
                policy = status.Policy.ToString(),
                verified = status.Verified,
                reason = status.Reason,
                tunnelDevice = status.TunnelDevice,
                heldTaskCount = status.HeldTaskCount,
                transitionSequence = status.TransitionSequence,
                observedAt = status.ObservedAt,
            });
        });

        group.MapPost("/{id:guid}/remove",
            async (Guid id, bool? deleteFiles, DownloadService service, CancellationToken cancellationToken) =>
                await service.RemoveAsync(new DownloadTaskId(id), deleteFiles ?? false, cancellationToken)
                    ? Results.NoContent()
                    : Results.NotFound());

        group.MapPut("/{id:guid}/priorities",
            async (Guid id, SetPrioritiesRequest request, DownloadService service, CancellationToken cancellationToken) =>
                await service.SetFilePrioritiesAsync(new DownloadTaskId(id), request.Priorities, cancellationToken)
                    ? Results.NoContent()
                    : Results.NotFound());

        return endpoints;
    }

    /// <summary>A control request the task's state does not allow: a conflict, and nothing was touched.</summary>
    private static IResult InvalidState(DownloadStateConflictException exception) =>
        Results.Conflict(new
        {
            error = "downloads.invalid_state",
            message = $"This download cannot do that: {exception.Reason}.",
        });

    // The published wire shape, so this response and the realtime progress snapshot are one object.
    private static object ToSummaryDto(DownloadTaskSummary task) => DownloadTaskWire.From(task);

    private static object ToDetailDto(DownloadTaskDetail detail) => new
    {
        task = ToSummaryDto(detail.Task),
        files = detail.Files.Select(f => new
        {
            index = f.Index,
            path = f.Path,
            size = f.Size,
            priority = f.Priority.ToString(),
        }),
        history = detail.History.Select(h => new
        {
            seq = h.Seq,
            from = h.From.ToString(),
            to = h.To.ToString(),
            trigger = h.Trigger,
            occurredAt = h.OccurredAt,
            note = h.Note,
        }),
    };
}

/// <summary>Body of the set-priorities request: a map of file index → priority.</summary>
public sealed record SetPrioritiesRequest(Dictionary<int, FilePriorityLevel> Priorities);
