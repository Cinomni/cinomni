using System.Security.Claims;
using Cinomni.Kernel.Results;
using Cinomni.Kernel.Security;
using Cinomni.Operations.Messaging;
using Cinomni.Subtitles.Application;
using Cinomni.Subtitles.Contracts;
using Cinomni.Subtitles.Messaging;
using Cinomni.Subtitles.Sync;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Cinomni.Subtitles.Api;

/// <summary>
/// HTTP surface of the Subtitles module: observe the subtitle searches for an asset and their
/// outcomes, and for an administrator, re-check a subtitle's timing or put the downloaded file back.
/// Searching is driven by <c>MediaAssetRegistered</c>, not by API (a manual search is a
/// later addition). Every read is scoped to what the caller may see (a search is visible when its asset's
/// work is), and the file's on-disk location is infrastructure detail only an operator gets (as in Library).
/// </summary>
public static class SubtitleEndpoints
{
    public static IEndpointRouteBuilder MapSubtitleEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/subtitles").RequireAuthorization();

        group.MapPost("/catch-up", async (ICommandQueue commandQueue, CancellationToken cancellationToken) =>
        {
            await commandQueue.EnqueueAsync(
                new CatchUpSubtitlesCommand(),
                idempotencyKey: $"subtitles-catch-up:{DateTimeOffset.UtcNow.UtcTicks}",
                cancellationToken);
            return Results.Accepted();
        }).RequireAuthorization(AuthorizationPolicies.Administrator);

        group.MapGet("/assets/{assetId:guid}", async (
            Guid assetId,
            ClaimsPrincipal principal,
            SubtitleBrowse browse,
            CancellationToken cancellationToken) =>
        {
            if (Viewer.From(principal) is not { } viewer)
            {
                return Results.Unauthorized();
            }

            var searches = await browse.ListForAssetAsync(viewer, assetId, cancellationToken);
            var obtained = (await browse.ListObtainedAsync(viewer, assetId, cancellationToken))
                .ToDictionary(subtitle => subtitle.Id.Value);
            return Results.Ok(searches.Select(search => ToSearchDto(
                search, search.SubtitleId is { } id ? obtained.GetValueOrDefault(id) : null)));
        });

        // Timing is the administrator's call: both change which file every viewer of the title is
        // served, and a re-sync spends minutes of FFmpeg.
        group.MapPost("/obtained/{id:guid}/resync", async (
            Guid id,
            SubtitleSyncCommands commands,
            CancellationToken cancellationToken) =>
        {
            var result = await commands.ResyncAsync(new SubtitleAssetId(id), cancellationToken);
            return result.IsSuccess ? Results.Accepted() : FailureResult(result.Error);
        }).RequireAuthorization(AuthorizationPolicies.Administrator);

        group.MapPost("/obtained/{id:guid}/revert", async (
            Guid id,
            SubtitleSyncCommands commands,
            CancellationToken cancellationToken) =>
        {
            var result = await commands.RevertAsync(new SubtitleAssetId(id), cancellationToken);
            return result.IsSuccess ? Results.NoContent() : FailureResult(result.Error);
        }).RequireAuthorization(AuthorizationPolicies.Administrator);

        group.MapGet("/searches/{id:guid}", async (
            Guid id,
            ClaimsPrincipal principal,
            SubtitleBrowse browse,
            CancellationToken cancellationToken) =>
        {
            if (Viewer.From(principal) is not { } viewer)
            {
                return Results.Unauthorized();
            }

            var detail = await browse.GetAsync(viewer, new SubtitleSearchId(id), cancellationToken);
            return detail is null ? Results.NotFound() : Results.Ok(ToDetailDto(detail, viewer.IsAdministrator));
        });

        return endpoints;
    }

    private static IResult FailureResult(Error error)
    {
        var status = error.Code == SubtitleSyncCommands.NotFound.Code
            ? StatusCodes.Status404NotFound
            : StatusCodes.Status409Conflict;
        return Results.Json(new { error = error.Code, message = error.Message }, statusCode: status);
    }

    private static object ToSearchDto(SubtitleSearchSummary search, SubtitleAssetSummary? subtitle = null) => new
    {
        id = search.Id.ToString(),
        assetId = search.AssetId.ToString(),
        language = search.Language,
        forced = search.Forced,
        hearingImpaired = search.HearingImpaired,
        state = search.State.ToString(),
        attempts = search.Attempts,
        // The subtitle this search landed, if any, and what synchronization made of it. No path: this
        // list is for every viewer of the title.
        subtitle = subtitle is null ? null : new
        {
            id = subtitle.Id.ToString(),
            format = subtitle.Format.ToString(),
            sync = ToSyncDto(subtitle.Sync),
        },
    };

    private static object? ToSyncDto(SubtitleSyncSummary? sync) => sync is null ? null : new
    {
        outcome = sync.Outcome.ToString(),
        offsetMs = sync.OffsetMs,
        ratio = sync.Ratio,
        score = Math.Round(sync.Score, 3),
        reason = sync.Reason,
        hasOriginal = sync.HasOriginal,
        decidedAt = sync.DecidedAt,
    };

    private static object ToDetailDto(SubtitleSearchDetail detail, bool isAdministrator) => new
    {
        search = ToSearchDto(detail.Search),
        candidates = detail.Candidates.Select(c => new
        {
            provider = c.Provider,
            release = c.Release,
            score = c.Score,
            hearingImpaired = c.HearingImpaired,
        }),
        asset = detail.Asset is null ? null : new
        {
            id = detail.Asset.Id.ToString(),
            language = detail.Asset.Language,
            forced = detail.Asset.Forced,
            hearingImpaired = detail.Asset.HearingImpaired,
            format = detail.Asset.Format.ToString(),
            // Where the file lives on the server says nothing to a viewer and everything to an attacker.
            path = isAdministrator ? detail.Asset.Path : null,
            provider = detail.Asset.Provider,
            score = detail.Asset.Score,
            sync = ToSyncDto(detail.Asset.Sync),
        },
    };
}
