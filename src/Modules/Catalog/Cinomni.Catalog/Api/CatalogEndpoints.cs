using System.Security.Claims;
using Cinomni.Catalog.Application;
using Cinomni.Catalog.Contracts;
using Cinomni.Catalog.Messaging;
using Cinomni.Catalog.Persistence;
using Cinomni.Kernel.Security;
using Cinomni.Metadata.Contracts;
using Cinomni.Operations.Messaging;
using Cinomni.Operations.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

// Aliased rather than imported: `using Cinomni.Kernel.Results` would make every `Results.Ok(...)` below
// ambiguous with that namespace.
using AddWorkResult = Cinomni.Kernel.Results.Result<Cinomni.Catalog.Contracts.WorkId>;

namespace Cinomni.Catalog.Api;

/// <summary>
/// HTTP surface of the Catalog module: browse the works you may see and walk a series' season and
/// episode tree (any signed-in account — this is the library), add a movie or a series (administrators
/// only; everyone else asks through Requests), and administer the collections works sit in and who
/// may browse them. Every route hangs off the one authorized group — a route added outside it would be
/// anonymous, and nothing in the build would say so.
/// <para>
/// Every read is answered for a viewer, never through the unscoped <c>ICatalogQuery</c>: a work in a
/// collection this caller was not granted is absent from the list, 404 by id, 404 by external id and 404
/// down its whole season/episode tree — the same answer a work that does not exist gives, so no endpoint
/// is an oracle.
/// </para>
/// </summary>
public static class CatalogEndpoints
{
    public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/catalog").RequireAuthorization();

        // The certificates an administrator may set as a ceiling, for the region this installation
        // currently classifies by. Empty when that region is unnamed or has no ladder — the page says
        // so rather than offering a control that would be refused.
        group.MapGet("/content-ratings", (IContentRatingRegion region) =>
        {
            var current = region.Current;
            return Results.Ok(new
            {
                region = string.IsNullOrWhiteSpace(current) ? null : current,
                certificates = ContentRatingScale.Certificates(current),
            });
        }).RequireAuthorization(AuthorizationPolicies.Administrator);

        // Add a movie to the catalog (step 2 of the vertical slice). Putting a title in the library is an
        // operator action: a regular account submits a request instead, which an administrator approves.
        group.MapPost("/works", async (AddMovieRequest request, ICatalogCommands commands, CancellationToken cancellationToken) =>
        {
            var externalIds = request.ExternalIds ?? [];
            var result = await commands.AddMovieAsync(
                request.Title, request.Year, externalIds, Shelf(request.CollectionId), cancellationToken: cancellationToken);
            return Created(result);
        })
            .RequireAuthorization(AuthorizationPolicies.Administrator);

        // Remove a work. The other modules let go of it on their own when WorkRemoved reaches them; the
        // files on disk are deleted only when the administrator asked for that too.
        group.MapDelete("/works/{id:guid}", async (
            Guid id, bool? deleteFiles, ICatalogCommands commands, CancellationToken cancellationToken) =>
        {
            var result = await commands.RemoveWorkAsync(new WorkId(id), deleteFiles ?? false, cancellationToken);
            return result.IsSuccess
                ? Results.NoContent()
                : Results.Json(
                    new { error = result.Error.Code, message = result.Error.Message },
                    statusCode: StatusCodes.Status404NotFound);
        })
            .RequireAuthorization(AuthorizationPolicies.Administrator);

        // Add a series. Same identity rules as a movie and the same single WorkAdded event; the
        // season/episode tree arrives later through the metadata refresh. Operator action, like adding a
        // movie — a regular account submits a request instead.
        group.MapPost("/series", async (AddSeriesRequest request, ICatalogCommands commands, CancellationToken cancellationToken) =>
        {
            var externalIds = request.ExternalIds ?? [];
            var result = await commands.AddSeriesAsync(
                request.Title, request.Year, externalIds, Shelf(request.CollectionId), cancellationToken: cancellationToken);
            return Created(result);
        })
            .RequireAuthorization(AuthorizationPolicies.Administrator);

        group.MapGet("/import-list", async (
            CatalogDbContext dbContext,
            ILiveOptions<TrendingListOptions> options,
            CancellationToken cancellationToken) =>
        {
            var entries = await dbContext.ImportListEntries
                .AsNoTracking()
                .OrderByDescending(e => e.LastSeenAt)
                .Take(50)
                .ToListAsync(cancellationToken);
            return Results.Ok(new
            {
                enabled = options.Current.Enabled,
                entries = entries.Select(e => new
                {
                    id = e.Id.ToString(),
                    provider = e.Provider,
                    externalId = e.ExternalId,
                    kind = e.Kind,
                    title = e.Title,
                    year = e.Year,
                    workId = e.WorkId?.ToString(),
                    outcome = e.Outcome,
                    firstSeenAt = e.FirstSeenAt,
                    lastSeenAt = e.LastSeenAt,
                }),
            });
        }).RequireAuthorization(AuthorizationPolicies.Administrator);

        group.MapPost("/import-list/refresh", async (ICommandQueue commandQueue, CancellationToken cancellationToken) =>
        {
            await commandQueue.EnqueueAsync(
                new RefreshTrendingListCommand(),
                idempotencyKey: $"trending-list:{DateTimeOffset.UtcNow.UtcTicks}",
                cancellationToken);
            return Results.Accepted();
        }).RequireAuthorization(AuthorizationPolicies.Administrator);

        group.MapGet("/works", async (
            Guid? collection,
            ClaimsPrincipal principal,
            ICatalogBrowse browse,
            CancellationToken cancellationToken) =>
        {
            if (Viewer.From(principal) is not { } viewer)
            {
                return Results.Unauthorized();
            }

            var works = await browse.ListAsync(viewer, Shelf(collection), cancellationToken);
            return Results.Ok(works.Select(work => ToDto(work)));
        });

        // The paged twin of the route above, filtered and ordered on the server so a client can walk a
        // large library a page at a time. A separate route rather than new parameters on `/works`, whose
        // bare-array answer other pages read.
        group.MapGet("/works/page", async (
            Guid? collection,
            string? kind,
            string? q,
            string? availability,
            string? genre,
            string? sort,
            int? offset,
            int? limit,
            ClaimsPrincipal principal,
            ICatalogBrowse browse,
            CancellationToken cancellationToken) =>
        {
            if (Viewer.From(principal) is not { } viewer)
            {
                return Results.Unauthorized();
            }

            if (!TryParseName<WorkKind>(kind, out var workKind)
                || !TryParseName<WorkAvailability>(availability, out var workAvailability)
                || !TryParseName<WorkSort>(sort, out var workSort))
            {
                return Results.Json(
                    new
                    {
                        error = "catalog.invalid_filter",
                        message = "kind, availability and sort take one of their named values.",
                    },
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var query = new WorkListQuery(
                Shelf(collection), workKind, q, workAvailability, genre, workSort ?? WorkSort.Title);
            var page = await browse.PageAsync(
                viewer, query, offset ?? 0, limit ?? CatalogPaging.DefaultPageSize, cancellationToken);
            return Results.Ok(new
            {
                items = page.Items.Select(work => ToDto(work)),
                total = page.Total,
                offset = page.Offset,
                limit = page.Limit,
            });
        });

        // Counts for the list's filter controls, over the same visible set the page route walks.
        group.MapGet("/works/facets", async (
            Guid? collection,
            string? kind,
            ClaimsPrincipal principal,
            ICatalogBrowse browse,
            CancellationToken cancellationToken) =>
        {
            if (Viewer.From(principal) is not { } viewer)
            {
                return Results.Unauthorized();
            }

            if (!TryParseName<WorkKind>(kind, out var workKind))
            {
                return Results.Json(
                    new { error = "catalog.invalid_filter", message = "kind takes Movie or Series." },
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var facets = await browse.FacetsAsync(viewer, Shelf(collection), workKind, cancellationToken);
            return Results.Ok(new
            {
                movies = facets.Movies,
                series = facets.Series,
                genres = facets.Genres.Select(g => new { genre = g.Genre, count = g.Count }),
            });
        });

        group.MapGet("/works/{id:guid}", async (
            Guid id,
            ClaimsPrincipal principal,
            ICatalogBrowse browse,
            ICatalogSeriesQuery seriesQuery,
            CancellationToken cancellationToken) =>
        {
            if (Viewer.From(principal) is not { } viewer)
            {
                return Results.Unauthorized();
            }

            var work = await browse.GetByIdAsync(viewer, new WorkId(id), cancellationToken);
            if (work is null)
            {
                return Results.NotFound();
            }

            // The detail page is one work, so counting its seasons is a bounded query. The list route
            // deliberately does not do this — it must stay a single flat scan of `works`.
            var seasonCount = work.Kind == WorkKind.Series
                ? (await seriesQuery.GetSeasonsAsync(new WorkId(id), cancellationToken)).Count
                : 0;

            return Results.Ok(ToDto(work, seasonCount, withOverview: true));
        });

        // `kind` narrows the answer to films or shows: a provider can number the two independently, so
        // the same id can name one of each. Omitted, the first match answers, as it always did.
        group.MapGet("/works/by-external/{provider}/{value}", async (
            MetadataProvider provider,
            string value,
            WorkKind? kind,
            ClaimsPrincipal principal,
            ICatalogBrowse browse,
            CancellationToken cancellationToken) =>
        {
            if (Viewer.From(principal) is not { } viewer)
            {
                return Results.Unauthorized();
            }

            var work = await browse.FindByExternalIdAsync(viewer, provider, value, kind, cancellationToken);
            return work is null ? Results.NotFound() : Results.Ok(ToDto(work));
        });

        MapSeriesRoutes(group);
        MapCollectionRoutes(group);

        return endpoints;
    }

    private static void MapSeriesRoutes(RouteGroupBuilder group)
    {
        // A season or an episode is part of its series, so it is visible exactly when the series is:
        // one access check on the work covers the whole tree, and a hidden series 404s all the way down.
        group.MapGet("/works/{id:guid}/seasons", async (
            Guid id,
            ClaimsPrincipal principal,
            IContentAccess access,
            ICatalogSeriesQuery query,
            CancellationToken cancellationToken) =>
        {
            if (await NotVisibleAsync(principal, access, id, cancellationToken) is { } refusal)
            {
                return refusal;
            }

            var seasons = await query.GetSeasonsAsync(new WorkId(id), cancellationToken);
            return Results.Ok(seasons.Select(ToDto));
        });

        group.MapGet("/works/{id:guid}/seasons/{number:int}/episodes", async (
            Guid id,
            int number,
            ClaimsPrincipal principal,
            IContentAccess access,
            ICatalogSeriesQuery query,
            CancellationToken cancellationToken) =>
        {
            if (await NotVisibleAsync(principal, access, id, cancellationToken) is { } refusal)
            {
                return refusal;
            }

            var episodes = await query.GetEpisodesAsync(new WorkId(id), number, cancellationToken);
            return Results.Ok(episodes.Select(ToDto));
        });

        group.MapGet("/works/{id:guid}/episodes/{episodeId:guid}", async (
            Guid id,
            Guid episodeId,
            ClaimsPrincipal principal,
            IContentAccess access,
            ICatalogSeriesQuery query,
            CancellationToken cancellationToken) =>
        {
            if (await NotVisibleAsync(principal, access, id, cancellationToken) is { } refusal)
            {
                return refusal;
            }

            var episode = await query.GetEpisodeAsync(new WorkId(id), new EpisodeId(episodeId), cancellationToken);
            return episode is null ? Results.NotFound() : Results.Ok(ToDto(episode));
        });
    }

    /// <summary>
    /// The refusal to return when this caller may not see the work, or null when they may. 404 rather
    /// than 403: hidden must be indistinguishable from missing.
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

    private static CollectionId? Shelf(Guid? id) => id is { } value ? new CollectionId(value) : null;

    private static void MapCollectionRoutes(RouteGroupBuilder group)
    {
        // Everyone sees the shelves they may browse; creating them and granting them is operator-only.
        group.MapGet("/collections", async (
            ClaimsPrincipal principal,
            ICatalogBrowse browse,
            CancellationToken cancellationToken) =>
        {
            if (Viewer.From(principal) is not { } viewer)
            {
                return Results.Unauthorized();
            }

            var collections = await browse.CollectionsAsync(viewer, cancellationToken);
            return Results.Ok(collections.Select(ToDto));
        });

        group.MapPost("/collections", async (
            CreateCollectionRequest request,
            ICollectionAdministration admin,
            CancellationToken cancellationToken) =>
        {
            var result = await admin.CreateAsync(request.Name, request.Kind, request.AccessMode, cancellationToken);
            return result.IsSuccess
                ? Results.Created($"/api/catalog/collections/{result.Value}", new { collectionId = result.Value.ToString() })
                : FailureResult(result.Error);
        })
            .RequireAuthorization(AuthorizationPolicies.Administrator);

        group.MapPut("/collections/{id:guid}/access-mode", async (
            Guid id,
            SetAccessModeRequest request,
            ICollectionAdministration admin,
            CancellationToken cancellationToken) =>
        {
            var result = await admin.SetAccessModeAsync(new CollectionId(id), request.AccessMode, cancellationToken);
            return result.IsSuccess ? Results.NoContent() : FailureResult(result.Error);
        })
            .RequireAuthorization(AuthorizationPolicies.Administrator);

        group.MapGet("/collections/{id:guid}/grants", async (
            Guid id,
            ICollectionAdministration admin,
            CancellationToken cancellationToken) =>
            Results.Ok((await admin.GrantedAccountsAsync(new CollectionId(id), cancellationToken))
                .Select(userId => userId.ToString())))
            .RequireAuthorization(AuthorizationPolicies.Administrator);

        group.MapPut("/collections/{id:guid}/grants/{userId:guid}", async (
            Guid id,
            Guid userId,
            ClaimsPrincipal principal,
            ICollectionAdministration admin,
            CancellationToken cancellationToken) =>
        {
            if (Viewer.From(principal) is not { } viewer)
            {
                return Results.Unauthorized();
            }

            var result = await admin.GrantAsync(new CollectionId(id), userId, viewer.UserId, cancellationToken);
            return result.IsSuccess ? Results.NoContent() : FailureResult(result.Error);
        })
            .RequireAuthorization(AuthorizationPolicies.Administrator);

        group.MapDelete("/collections/{id:guid}/grants/{userId:guid}", async (
            Guid id,
            Guid userId,
            ICollectionAdministration admin,
            CancellationToken cancellationToken) =>
        {
            var result = await admin.RevokeAsync(new CollectionId(id), userId, cancellationToken);
            return result.IsSuccess ? Results.NoContent() : FailureResult(result.Error);
        })
            .RequireAuthorization(AuthorizationPolicies.Administrator);

        group.MapPut("/works/{id:guid}/collection", async (
            Guid id,
            MoveWorkRequest request,
            ICollectionAdministration admin,
            CancellationToken cancellationToken) =>
        {
            var result = await admin.MoveWorkAsync(new WorkId(id), new CollectionId(request.CollectionId), cancellationToken);
            return result.IsSuccess ? Results.NoContent() : FailureResult(result.Error);
        })
            .RequireAuthorization(AuthorizationPolicies.Administrator);

        group.MapCollectionRuleRoutes();
    }

    internal static IResult FailureResult(Cinomni.Kernel.Results.Error error)
    {
        var status = error.Code switch
        {
            "catalog.collection_not_found" or "catalog.work_not_found" => StatusCodes.Status404NotFound,
            "catalog.collection_exists" => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest,
        };

        return Results.Json(new { error = error.Code, message = error.Message }, statusCode: status);
    }

    private static object ToDto(CollectionSummary collection) => new
    {
        id = collection.Id.ToString(),
        name = collection.Name,
        kind = collection.Kind.ToString(),
        accessMode = collection.AccessMode.ToString(),
        isDefault = collection.IsDefault,
        workCount = collection.WorkCount,
        rulePriority = collection.RulePriority,
    };

    /// <summary>
    /// Reads an optional enum query value by its name, ignoring case. Absent is fine (null); a number or an
    /// unknown name is refused — <c>Enum.TryParse</c> alone would accept "7" as a value no member has.
    /// </summary>
    private static bool TryParseName<T>(string? raw, out T? value)
        where T : struct, Enum
    {
        value = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        // IsDefined as well: TryParse also reads "Movie,Series" as the bitwise union of the two.
        if (!char.IsLetter(raw.Trim()[0])
            || !Enum.TryParse<T>(raw.Trim(), ignoreCase: true, out var parsed)
            || !Enum.IsDefined(parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static IResult Created(AddWorkResult result) =>
        result.IsSuccess
            ? Results.Created($"/api/catalog/works/{result.Value}", new { workId = result.Value.ToString() })
            : Results.Json(
                new { error = result.Error.Code, message = result.Error.Message },
                statusCode: StatusCodes.Status400BadRequest);

    /// <summary>
    /// Projects a work for the API. <c>web/src/api/types.ts</c> mirrors this field-for-field with no
    /// generated client and no contract test, so members may only be <b>added</b> — a rename breaks the
    /// SPA at runtime with no compile signal.
    /// <para>
    /// The synopsis is served on the detail route only (<paramref name="withOverview"/>): it runs to
    /// thousands of characters, and the list routes answer for a whole library at once. Elsewhere
    /// <c>overview</c> is null.
    /// </para>
    /// </summary>
    private static object ToDto(WorkSummary work, int seasonCount = 0, bool withOverview = false) => new
    {
        id = work.Id.ToString(),
        kind = work.Kind.ToString(),
        title = work.Title,
        year = work.Year,
        status = work.Status.ToString(),
        hasAsset = work.HasAsset,
        posterUrl = work.PosterUrl,
        backdropUrl = work.BackdropUrl,
        metadataSnapshotId = work.MetadataSnapshotId?.ToString(),
        seasonCount,
        episodeCount = work.EpisodeCount,
        availableEpisodeCount = work.AvailableEpisodeCount,
        collectionId = work.Collection?.ToString(),
        externalIds = work.ExternalIds.Select(e => new { provider = e.Provider.ToString(), value = e.Value }),
        overview = withOverview ? work.Overview : null,
        runtimeMinutes = work.RuntimeMinutes,
        genres = work.Genres ?? [],
        // Smaller renditions of the two urls above, for what a client actually draws. A provider with no
        // size ladder answers its original here too, so these are always safe to render.
        posterSmallUrl = ArtworkVariants.Resize(work.PosterUrl, ArtworkSize.PosterSmall),
        backdropSmallUrl = ArtworkVariants.Resize(work.BackdropUrl, ArtworkSize.BackdropSmall),
        backdropLargeUrl = ArtworkVariants.Resize(work.BackdropUrl, ArtworkSize.BackdropLarge),
    };

    private static object ToDto(SeasonSummary season) => new
    {
        id = season.Id.ToString(),
        workId = season.WorkId.ToString(),
        number = season.Number,
        title = season.Title,
        airDate = season.AirDate,
        expectedEpisodeCount = season.ExpectedEpisodeCount,
        posterUrl = season.PosterUrl,
        posterSmallUrl = ArtworkVariants.Resize(season.PosterUrl, ArtworkSize.PosterSmall),
    };

    private static object ToDto(EpisodeSummary episode) => new
    {
        id = episode.Id.ToString(),
        seasonId = episode.SeasonId.ToString(),
        workId = episode.WorkId.ToString(),
        seasonNumber = episode.SeasonNumber,
        number = episode.Number,
        absoluteNumber = episode.AbsoluteNumber,
        title = episode.Title,
        airDate = episode.AirDate,
        airDateTime = episode.AirDateTime,
        runtimeMinutes = episode.RuntimeMinutes,
        hasAsset = episode.HasAsset,
    };

    public sealed record AddMovieRequest(string Title, int? Year, List<ExternalId>? ExternalIds, Guid? CollectionId);

    public sealed record AddSeriesRequest(string Title, int? Year, List<ExternalId>? ExternalIds, Guid? CollectionId);

    public sealed record CreateCollectionRequest(string Name, CollectionKind Kind, CollectionAccessMode AccessMode);

    public sealed record SetAccessModeRequest(CollectionAccessMode AccessMode);

    public sealed record MoveWorkRequest(Guid CollectionId);
}
