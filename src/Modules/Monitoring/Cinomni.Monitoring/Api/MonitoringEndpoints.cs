using System.Security.Claims;
using Cinomni.Catalog.Contracts;
using Cinomni.Kernel.Security;
using Cinomni.Monitoring.Application;
using Cinomni.Monitoring.Contracts;
using Cinomni.Monitoring.Messaging;
using Cinomni.Operations.Messaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

// Aliased: the ASP.NET static Results class and the Cinomni.Kernel.Results namespace collide by name.
using CommandResult = Cinomni.Kernel.Results.Result;

namespace Cinomni.Monitoring.Api;

/// <summary>
/// HTTP surface of the Monitoring module: inspect targets and walk a series' target tree (any signed-in
/// user — the work detail shows whether a title is being watched), and apply a policy or toggle a target
/// (administrators only). Every route hangs off the group that calls <c>RequireAuthorization()</c>, and a
/// route added outside it would be anonymous with no build signal.
/// <para>
/// The two global listings (<c>/targets</c>, <c>/targets/missing</c>) are scoped to the caller through
/// <see cref="MonitoringBrowse"/>: a title in a collection the caller was never granted must not be
/// enumerable through its target row, so both bind the viewer and narrow through Catalog's content-access
/// authority before paging. The per-work routes below are scoped the same way, one level down: knowing a
/// work's id must not grant its monitoring state when the work itself is hidden, so both answer 404 for a
/// work the caller may not see — the same shape as a work that does not exist at all, never 403, so hidden
/// stays indistinguishable from missing.
/// </para>
/// </summary>
public static class MonitoringEndpoints
{
    public static IEndpointRouteBuilder MapMonitoringEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/monitoring").RequireAuthorization();

        // Apply a monitoring policy to a work (step 3 of the vertical slice).
        group.MapPut("/works/{workId:guid}/policy",
            async (Guid workId, ApplyPolicyRequest request, IMonitoringCommands commands, CancellationToken cancellationToken) =>
            {
                var result = await commands.ApplyMonitoringPolicyAsync(new WorkId(workId), request.Mode, cancellationToken);
                if (result.IsSuccess)
                {
                    return Results.Ok(new { targetId = result.Value.ToString() });
                }

                var status = result.Error.Code == "monitoring.unknown_work"
                    ? StatusCodes.Status404NotFound
                    : StatusCodes.Status400BadRequest;
                return Results.Json(new { error = result.Error.Code, message = result.Error.Message }, statusCode: status);
            })
            .RequireAuthorization(AuthorizationPolicies.Administrator);

        group.MapPut("/targets/{targetId:guid}/monitored",
            async (Guid targetId, SetMonitoredRequest request, IMonitoringCommands commands, CancellationToken cancellationToken) =>
            {
                var result = await commands.SetTargetMonitoredAsync(
                    new MonitoredTargetId(targetId), request.Monitored, cancellationToken);
                return NoContentOrNotFound(result);
            })
            .RequireAuthorization(AuthorizationPolicies.Administrator);

        // Toggle a whole branch: a series root cascades to its seasons and their episodes.
        group.MapPut("/targets/{targetId:guid}/subtree-monitored",
            async (Guid targetId, SetMonitoredRequest request, IMonitoringCommands commands, CancellationToken cancellationToken) =>
            {
                var result = await commands.SetSubtreeMonitoredAsync(
                    new MonitoredTargetId(targetId), request.Monitored, cancellationToken);
                return NoContentOrNotFound(result);
            })
            .RequireAuthorization(AuthorizationPolicies.Administrator);

        // Paginated: a single series contributes hundreds of targets, so these were unbounded before.
        // Scoped to the caller: an unfiltered listing would let a member enumerate titles they were never
        // granted by their target rows. The per-work routes below answer 404 for the same reason.
        group.MapGet("/targets", async (
            int? limit,
            int? offset,
            ClaimsPrincipal principal,
            MonitoringBrowse browse,
            CancellationToken cancellationToken) =>
        {
            if (Viewer.From(principal) is not { } viewer)
            {
                return Results.Unauthorized();
            }

            var targets = await browse.ListAsync(
                viewer, limit ?? MonitoringPaging.DefaultPageSize, offset ?? 0, cancellationToken);
            return Results.Ok(targets.Select(ToDto));
        });

        group.MapGet("/calendar", async (
            DateOnly? from,
            DateOnly? to,
            ClaimsPrincipal principal,
            CalendarReader calendar,
            CancellationToken cancellationToken) =>
        {
            if (Viewer.From(principal) is not { } viewer)
            {
                return Results.Unauthorized();
            }

            if (!CalendarWindow.TryCreate(from, to, DateOnly.FromDateTime(DateTime.UtcNow), out var start, out var end, out var error))
            {
                return Results.Json(
                    new { error = "monitoring.invalid_window", message = error },
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var entries = await calendar.ListAsync(viewer, start, end, cancellationToken);
            return Results.Ok(entries.Select(ToCalendar));
        });

        group.MapGet("/targets/missing", async (
            int? limit,
            Guid? workId,
            ClaimsPrincipal principal,
            MonitoringBrowse browse,
            CancellationToken cancellationToken) =>
        {
            if (Viewer.From(principal) is not { } viewer)
            {
                return Results.Unauthorized();
            }

            var targets = await browse.ListMissingAsync(
                viewer,
                limit ?? MonitoringPaging.DefaultPageSize,
                workId is { } scope ? new WorkId(scope) : null,
                cancellationToken);
            return Results.Ok(targets.Select(ToDto));
        });

        // Exactly one object, the work's root. The web client reads this shape directly, so the tree
        // deliberately gets its own route below rather than widening this one into an array.
        group.MapGet("/works/{workId:guid}/target",
            async (
                Guid workId,
                ClaimsPrincipal principal,
                IContentAccess access,
                IMonitoringQuery query,
                CancellationToken cancellationToken) =>
            {
                if (await NotVisibleAsync(principal, access, workId, cancellationToken) is { } refusal)
                {
                    return refusal;
                }

                var target = await query.GetByWorkAsync(new WorkId(workId), cancellationToken);
                return target is null ? Results.NotFound() : Results.Ok(ToDto(target));
            });

        group.MapGet("/works/{workId:guid}/targets",
            async (
                Guid workId,
                ClaimsPrincipal principal,
                IContentAccess access,
                IMonitoringQuery query,
                CancellationToken cancellationToken) =>
            {
                if (await NotVisibleAsync(principal, access, workId, cancellationToken) is { } refusal)
                {
                    return refusal;
                }

                var targets = await query.ListByWorkAsync(new WorkId(workId), cancellationToken);
                return targets.Count == 0 ? Results.NotFound() : Results.Ok(ToTree(targets));
            });

        // Manual search for one target — what "retry now" on an acquisition goal asks for. Same rules as
        // the season trigger below: the cooldown is cleared and the sweep decides what is actually asked.
        group.MapPost("/targets/{targetId:guid}/search",
            async (Guid targetId,
                IMonitoringCommands commands,
                ICommandQueue commandQueue,
                CancellationToken cancellationToken) =>
            {
                var cleared = await commands.ClearTargetSearchCooldownAsync(new MonitoredTargetId(targetId), cancellationToken);
                if (cleared.IsFailure)
                {
                    return Results.Json(
                        new { error = cleared.Error.Code, message = cleared.Error.Message },
                        statusCode: StatusCodes.Status404NotFound);
                }

                await commandQueue.EnqueueAsync(
                    new EvaluateMissingCommand(),
                    idempotencyKey: $"evaluate-missing:target:{targetId}:{DateTimeOffset.UtcNow.UtcTicks}",
                    cancellationToken);

                return Results.Accepted();
            })
            .RequireAuthorization(AuthorizationPolicies.Administrator);

        // Manual, season-scoped search trigger: clears the season's cooldown stamp and runs the sweep, so
        // the same granularity and quota rules apply as on the scheduled tick.
        group.MapPost("/works/{workId:guid}/seasons/{seasonNumber:int}/search",
            async (Guid workId,
                int seasonNumber,
                IMonitoringCommands commands,
                ICommandQueue commandQueue,
                CancellationToken cancellationToken) =>
            {
                var cleared = await commands.ClearSearchCooldownAsync(
                    new WorkId(workId), seasonNumber, cancellationToken);
                if (cleared.IsFailure)
                {
                    return Results.Json(
                        new { error = cleared.Error.Code, message = cleared.Error.Message },
                        statusCode: StatusCodes.Status404NotFound);
                }

                // The instant is part of the key so a user may retry; the sweep itself stays idempotent
                // through the search window.
                await commandQueue.EnqueueAsync(
                    new EvaluateMissingCommand(),
                    idempotencyKey: $"evaluate-missing:{workId}:{seasonNumber}:{DateTimeOffset.UtcNow.UtcTicks}",
                    cancellationToken);

                return Results.Accepted();
            })
            .RequireAuthorization(AuthorizationPolicies.Administrator);

        return endpoints;
    }

    /// <summary>
    /// The refusal to return when this caller may not see the work, or null when they may. 404 rather than
    /// 403, exactly like Catalog's own per-work reads: hidden must be indistinguishable from missing, or
    /// the response code itself becomes the enumeration oracle the scoped listings above already close.
    /// </summary>
    private static async Task<IResult?> NotVisibleAsync(
        ClaimsPrincipal principal,
        IContentAccess access,
        Guid workId,
        CancellationToken cancellationToken)
    {
        if (Viewer.From(principal) is not { } viewer)
        {
            return Results.Unauthorized();
        }

        return await access.CanSeeWorkAsync(viewer, workId, cancellationToken) ? null : Results.NotFound();
    }

    private static IResult NoContentOrNotFound(CommandResult result) =>
        result.IsSuccess
            ? Results.NoContent()
            : Results.Json(new { error = result.Error.Code, message = result.Error.Message },
                statusCode: StatusCodes.Status404NotFound);

    /// <summary>The work's targets grouped by season, with the root's rollup counters at the top.</summary>
    private static object ToTree(IReadOnlyList<MonitoredTargetSummary> targets)
    {
        var root = targets.FirstOrDefault(t => t.Kind is TargetKind.Series or TargetKind.Movie);
        var episodesByParent = targets
            .Where(t => t.Kind == TargetKind.Episode && t.ParentId is not null)
            .GroupBy(t => t.ParentId!.Value)
            .ToDictionary(group => group.Key, group => group.ToList());

        return new
        {
            root = root is null ? null : ToDto(root),
            seasons = targets
                .Where(t => t.Kind == TargetKind.Season)
                .Select(season => new
                {
                    target = ToDto(season),
                    episodes = episodesByParent.TryGetValue(season.Id, out var episodes)
                        ? episodes.ConvertAll(ToDto)
                        : new List<object>(),
                })
                .ToList(),
        };
    }

    private static object ToCalendar(CalendarEntry entry) => new
    {
        id = entry.TargetId.ToString(),
        workId = entry.WorkId.ToString(),
        workTitle = entry.WorkTitle,
        kind = entry.Kind.ToString(),
        seasonNumber = entry.SeasonNumber,
        episodeNumber = entry.EpisodeNumber,
        title = entry.EpisodeTitle,
        airDate = entry.AirDate.ToString("yyyy-MM-dd"),
        airDateTime = entry.AirsAt,
        monitored = entry.Monitored,
        isMissing = entry.IsMissing,
    };

    private static object ToDto(MonitoredTargetSummary target) => new
    {
        id = target.Id.ToString(),
        workId = target.WorkId.ToString(),
        kind = target.Kind.ToString(),
        monitored = target.Monitored,
        mode = target.Mode.ToString(),
        isMissing = target.IsMissing,
        targetRef = target.TargetRef.ToString(),
        parentId = target.ParentId?.ToString(),
        seasonNumber = target.SeasonNumber,
        episodeNumber = target.EpisodeNumber,
        absoluteNumber = target.AbsoluteNumber,
        airDate = target.AirDate,
        title = target.Title,
        missingCount = target.MissingCount,
        totalCount = target.TotalCount,
    };

    public sealed record ApplyPolicyRequest(MonitoringMode Mode);

    public sealed record SetMonitoredRequest(bool Monitored);
}
