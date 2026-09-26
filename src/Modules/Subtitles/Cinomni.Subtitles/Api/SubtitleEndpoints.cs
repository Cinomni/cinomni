using System.Security.Claims;
using Cinomni.Kernel.Security;
using Cinomni.Operations.Messaging;
using Cinomni.Subtitles.Application;
using Cinomni.Subtitles.Contracts;
using Cinomni.Subtitles.Messaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Cinomni.Subtitles.Api;

/// <summary>
/// HTTP surface of the Subtitles module: observe the subtitle searches for an asset and their
/// outcomes. Searching is driven by <c>MediaAssetRegistered</c>, not by API (a manual search is a
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
            return Results.Ok(searches.Select(ToSearchDto));
        });

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

    private static object ToSearchDto(SubtitleSearchSummary search) => new
    {
        id = search.Id.ToString(),
        assetId = search.AssetId.ToString(),
        language = search.Language,
        forced = search.Forced,
        hearingImpaired = search.HearingImpaired,
        state = search.State.ToString(),
        attempts = search.Attempts,
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
        },
    };
}
