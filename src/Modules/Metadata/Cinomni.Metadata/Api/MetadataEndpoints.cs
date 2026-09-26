using Cinomni.Kernel.Identifiers;
using Cinomni.Kernel.Security;
using Cinomni.Metadata.Contracts;
using Cinomni.Metadata.Messaging;
using Cinomni.Operations.Messaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Cinomni.Metadata.Api;

/// <summary>
/// HTTP surface of the Metadata module: search providers for a term, trigger a refresh for a work, read
/// a snapshot (with its artwork), and override the selected artwork. The client orchestrates enrichment
/// (search → add work in Catalog → refresh); Catalog then consumes <c>MetadataRefreshed</c> to copy the
/// snapshot onto its work, and <c>MetadataArtworkSelected</c> to update its artwork.
/// Reads are open to any signed-in user (searching is how they pick a title to request); the writes that
/// spend a provider call or change what everyone sees are administrator-only.
/// </summary>
public static class MetadataEndpoints
{
    /// <summary>Body of a refresh request: which work to enrich, from which provider, external id and kind.</summary>
    public sealed record RefreshMetadataRequest(
        Guid WorkId,
        string Provider,
        string ExternalId,
        MetadataMediaKind Kind = MetadataMediaKind.Movie);

    public static IEndpointRouteBuilder MapMetadataEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/metadata").RequireAuthorization();

        group.MapGet("/search", async (string term, int? year, MetadataMediaKind? kind, IMetadataSearch search, CancellationToken cancellationToken) =>
        {
            var result = await search.SearchAsync(term, year, kind ?? MetadataMediaKind.Movie, cancellationToken);

            // A search nobody could answer is 503, not an empty 200: the request was valid and the
            // installation is what cannot honour it. An empty 200 stays exactly what it always was — the
            // providers were asked and none matched.
            return result.IsSuccess
                ? Results.Ok(result.Value.Select(ToCandidateDto))
                : Results.Json(
                    new { error = result.Error.Code, message = result.Error.Message },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
        });

        // Spending a provider call on a work, and overriding its artwork, are operator actions.
        group.MapPost("/refresh", async (RefreshMetadataRequest request, ICommandQueue commandQueue, CancellationToken cancellationToken) =>
        {
            await commandQueue.EnqueueAsync(
                new RefreshMetadataCommand(request.WorkId, request.Provider, request.ExternalId, request.Kind),
                idempotencyKey: $"metadata-refresh:{Uuid7.New()}",
                cancellationToken);
            return Results.Accepted();
        })
            .RequireAuthorization(AuthorizationPolicies.Administrator);

        // Both snapshot reads are the operator's: the artwork picker is their only reader. A snapshot names
        // its work, and Metadata may not ask Catalog who may see that work (the dependency points the other
        // way), so a member reads a title's details through Catalog, which filters by content access.
        group.MapGet("/snapshots/{id:guid}", async (Guid id, IMetadataQuery query, CancellationToken cancellationToken) =>
        {
            var snapshot = await query.GetSnapshotAsync(new MetadataSnapshotId(id), cancellationToken);
            return snapshot is null ? Results.NotFound() : Results.Ok(ToSnapshotDto(snapshot));
        })
            .RequireAuthorization(AuthorizationPolicies.Administrator);

        // The season/episode tree is its own route on purpose: joining it onto the snapshot read would
        // multiply artwork by episodes on a path the movie detail page hits on every render.
        group.MapGet("/snapshots/{id:guid}/structure", async (Guid id, IMetadataQuery query, CancellationToken cancellationToken) =>
        {
            var structure = await query.GetSeriesStructureAsync(new MetadataSnapshotId(id), cancellationToken);
            return structure is null ? Results.NotFound() : Results.Ok(ToStructureDto(structure));
        })
            .RequireAuthorization(AuthorizationPolicies.Administrator);

        group.MapPost("/snapshots/{id:guid}/artwork/{artworkId:guid}/select",
            async (Guid id, Guid artworkId, IMetadataArtwork artwork, CancellationToken cancellationToken) =>
            {
                var result = await artwork.SelectAsync(new MetadataSnapshotId(id), artworkId, cancellationToken);
                return result.IsSuccess
                    ? Results.NoContent()
                    : Results.NotFound(new { error = result.Error.Code, message = result.Error.Message });
            })
            .RequireAuthorization(AuthorizationPolicies.Administrator);

        return endpoints;
    }

    private static object ToCandidateDto(MetadataCandidate candidate) => new
    {
        provider = candidate.Provider,
        kind = candidate.Kind.ToString(),
        externalId = candidate.ExternalId,
        title = candidate.Title,
        year = candidate.Year,
        overview = candidate.Overview,
        // The cross-references a candidate was merged on — the client shows one row per show, not one
        // row per provider, and needs the ids to add the work with every identity it is known by.
        tvdbId = candidate.TvdbId,
        imdbId = candidate.ImdbId,
        tmdbId = candidate.TmdbId,
    };

    private static object ToSnapshotDto(MetadataSnapshot snapshot) => new
    {
        id = snapshot.Id.ToString(),
        workId = snapshot.WorkId.ToString(),
        provider = snapshot.Provider,
        kind = snapshot.Kind.ToString(),
        externalId = snapshot.ExternalId,
        title = snapshot.Title,
        originalTitle = snapshot.OriginalTitle,
        year = snapshot.Year,
        overview = snapshot.Overview,
        runtimeMinutes = snapshot.RuntimeMinutes,
        originalLanguage = snapshot.OriginalLanguage,
        posterUrl = snapshot.PosterUrl,
        backdropUrl = snapshot.BackdropUrl,
        fetchedAt = snapshot.FetchedAt,
        seriesStatus = snapshot.SeriesStatus?.ToString(),
        firstAired = snapshot.FirstAired,
        lastAired = snapshot.LastAired,
        tvdbId = snapshot.TvdbId,
        imdbId = snapshot.ImdbId,
        tmdbId = snapshot.TmdbId,
        seasonOrder = snapshot.SeasonOrder,
        artwork = snapshot.Artwork.Select(ToArtworkDto),
    };

    private static object ToArtworkDto(MetadataArtwork artwork) => new
    {
        id = artwork.Id.ToString(),
        kind = artwork.Kind.ToString(),
        url = artwork.Url,
        // What the picker draws: a candidate grid, never full size. Posters (and logos) at the poster
        // rendition, landscape art at the small backdrop one; the original where the provider has no ladder.
        thumbnailUrl = ArtworkVariants.Resize(
            artwork.Url,
            artwork.Kind is ArtworkKind.Backdrop or ArtworkKind.Still ? ArtworkSize.BackdropSmall : ArtworkSize.PosterSmall),
        language = artwork.Language,
        width = artwork.Width,
        height = artwork.Height,
        voteAverage = artwork.VoteAverage,
        voteCount = artwork.VoteCount,
        isSelected = artwork.IsSelected,
        // The scope the candidate is selected within: both null for series-level (and movie) artwork.
        seasonNumber = artwork.SeasonNumber,
        episodeNumber = artwork.EpisodeNumber,
    };

    private static object ToStructureDto(MetadataSeriesStructure structure) => new
    {
        snapshotId = structure.SnapshotId.ToString(),
        seasons = structure.Seasons.Select(season => new
        {
            number = season.Number,
            title = season.Title,
            overview = season.Overview,
            episodeCount = season.EpisodeCount,
            airDate = season.AirDate,
            posterUrl = season.PosterUrl,
            externalId = season.ExternalId,
        }),
        episodes = structure.Episodes.Select(episode => new
        {
            seasonNumber = episode.SeasonNumber,
            number = episode.Number,
            title = episode.Title,
            overview = episode.Overview,
            absoluteNumber = episode.AbsoluteNumber,
            airDate = episode.AirDate,
            airDateTime = episode.AirDateTime,
            runtimeMinutes = episode.RuntimeMinutes,
            stillUrl = episode.StillUrl,
            externalId = episode.ExternalId,
            isSpecial = episode.IsSpecial,
        }),
    };
}
