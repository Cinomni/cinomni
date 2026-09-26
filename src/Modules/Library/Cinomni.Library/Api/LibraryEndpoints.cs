using System.Security.Claims;
using Cinomni.Kernel.Security;
using Cinomni.Library.Application;
using Cinomni.Library.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Cinomni.Library.Api;

/// <summary>
/// HTTP surface of the Library module: browse the registered media assets and their relational
/// streams. Registration is driven by <c>MediaAvailable</c>, not by API. Open to any signed-in account —
/// it is what the library and the player read — except the on-disk location, which is infrastructure
/// detail only an operator gets.
/// </summary>
public static class LibraryEndpoints
{
    public static IEndpointRouteBuilder MapLibraryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/library/assets").RequireAuthorization();

        group.MapGet("/", async (
            Guid? workId,
            Guid? unitId,
            ClaimsPrincipal principal,
            LibraryBrowse browse,
            CancellationToken cancellationToken) =>
        {
            if (Viewer.From(principal) is not { } viewer)
            {
                return Results.Unauthorized();
            }

            var assets = await browse.ListAsync(viewer, workId, unitId, cancellationToken);
            return Results.Ok(assets.Select(ToSummaryDto));
        });

        group.MapGet("/{id:guid}", async (
            Guid id,
            ClaimsPrincipal principal,
            LibraryBrowse browse,
            CancellationToken cancellationToken) =>
        {
            if (Viewer.From(principal) is not { } viewer)
            {
                return Results.Unauthorized();
            }

            var detail = await browse.GetAsync(viewer, new MediaAssetId(id), cancellationToken);
            return detail is null ? Results.NotFound() : Results.Ok(ToDetailDto(detail, viewer.IsAdministrator));
        });

        return endpoints;
    }

    private static object ToSummaryDto(MediaAssetSummary asset) => new
    {
        id = asset.Id.ToString(),
        workId = asset.WorkId.ToString(),
        state = asset.State.ToString(),
        primaryVersionId = asset.PrimaryVersionId?.ToString(),
        createdAt = asset.CreatedAt,
        unitIds = (asset.UnitIds ?? []).Select(u => u.ToString()),
    };

    private static object ToDetailDto(MediaAssetDetail detail, bool isAdministrator) => new
    {
        asset = ToSummaryDto(detail.Asset),
        targetIds = detail.TargetIds.Select(t => t.ToString()),
        unitIds = (detail.UnitIds ?? []).Select(u => u.ToString()),
        versions = detail.Versions.Select(v => new
        {
            id = v.Id.ToString(),
            relativePath = v.RelativePath,
            // Where the file lives on the server says nothing to a viewer and everything to an attacker.
            fullPath = isAdministrator ? v.FullPath : null,
            size = v.Size,
            releaseGroup = v.ReleaseGroup,
            streams = v.Streams.Select(s => new
            {
                streamIndex = s.StreamIndex,
                type = s.Type.ToString(),
                codec = s.Codec,
                language = s.Language,
                channels = s.Channels,
                width = s.Width,
                height = s.Height,
                bitDepth = s.BitDepth,
                videoRangeType = s.VideoRangeType?.ToString(),
                isDefault = s.IsDefault,
                isForced = s.IsForced,
                isExternal = s.IsExternal,
            }),
        }),
    };
}
