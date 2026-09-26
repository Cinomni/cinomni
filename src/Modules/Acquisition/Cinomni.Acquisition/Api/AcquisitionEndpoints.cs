using Cinomni.Acquisition.Contracts;
using Cinomni.Kernel.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Cinomni.Acquisition.Api;

/// <summary>
/// HTTP surface of the Acquisition module: observe the persistent goals and their attempt/history
/// trail. Goals are driven by events (MonitoringEnabled, ReleaseSelected); the one write is an
/// administrator asking a goal to try again.
/// </summary>
public static class AcquisitionEndpoints
{
    public static IEndpointRouteBuilder MapAcquisitionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/acquisition").RequireAuthorization(AuthorizationPolicies.Administrator);

        group.MapGet("/intents", async (IAcquisitionQuery query, CancellationToken cancellationToken) =>
            Results.Ok((await query.ListAsync(cancellationToken)).Select(ToSummaryDto)));

        group.MapGet("/intents/{id:guid}",
            async (Guid id, IAcquisitionQuery query, CancellationToken cancellationToken) =>
            {
                var detail = await query.GetAsync(id, cancellationToken);
                return detail is null ? Results.NotFound() : Results.Ok(ToDetailDto(detail));
            });

        group.MapPost("/intents/{id:guid}/retry",
            async (Guid id, IAcquisitionCommands commands, CancellationToken cancellationToken) =>
            {
                var result = await commands.RetryAsync(id, cancellationToken);
                if (result.IsSuccess)
                {
                    return Results.Ok(ToSummaryDto(result.Value));
                }

                return Results.Json(
                    new { error = result.Error.Code, message = result.Error.Message },
                    statusCode: result.Error.Code == AcquisitionErrors.IntentNotFound
                        ? StatusCodes.Status404NotFound
                        : StatusCodes.Status409Conflict);
            });

        return endpoints;
    }

    private static object ToSummaryDto(AcquisitionIntentSummary intent) => new
    {
        id = intent.Id.ToString(),
        targetId = intent.TargetId,
        workId = intent.WorkId,
        state = intent.State.ToString(),
        attemptCount = intent.AttemptCount,
        maxAttempts = intent.MaxAttempts,
        selectedReleaseGuid = intent.SelectedReleaseGuid,
    };

    private static object ToDetailDto(AcquisitionIntentDetail detail) => new
    {
        intent = ToSummaryDto(detail.Intent),
        attempts = detail.Attempts.Select(a => new
        {
            id = a.Id.ToString(),
            ordinal = a.Ordinal,
            releaseGuid = a.ReleaseGuid,
            state = a.State.ToString(),
            startedAt = a.StartedAt,
            closedAt = a.ClosedAt,
            failureReason = a.FailureReason,
            release = a.Release is null
                ? null
                : new
                {
                    title = a.Release.Title,
                    indexerName = a.Release.IndexerName,
                    seeders = a.Release.Seeders,
                    leechers = a.Release.Leechers,
                },
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
