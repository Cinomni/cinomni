using Cinomni.Discovery.Application;
using Cinomni.Discovery.Contracts;
using Cinomni.Kernel.Results;
using Cinomni.Kernel.Security;
using Cinomni.Search.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Cinomni.Discovery.Api;

/// <summary>HTTP surface of the Discovery module: manage indexers and run a search. Requires authentication.</summary>
public static class DiscoveryEndpoints
{
    public static IEndpointRouteBuilder MapDiscoveryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/discovery").RequireAuthorization(AuthorizationPolicies.Administrator);

        MapCatalogSourceEndpoints(group);

        // Every entry of every enabled source. Cinomni ships no source, so this is empty until an
        // administrator subscribes to one.
        group.MapGet("/indexer-catalog", async (
            IIndexerAdministration admin, CancellationToken cancellationToken) =>
            Results.Ok((await admin.ListCatalogAsync(cancellationToken)).Select(ToDto)));

        group.MapPost("/indexer-catalog/{sourceId:guid}/{key}/install", async (
            Guid sourceId, string key, CatalogDraftRequest request, IIndexerAdministration admin,
            CancellationToken cancellationToken) =>
        {
            var result = await admin.InstallCatalogIndexerAsync(new IndexerCatalogSourceId(sourceId),
                key, request.Name, request.BaseUrl, request.Priority, request.Settings, cancellationToken);
            return result.IsSuccess
                ? Results.Ok(new { indexerId = result.Value.ToString() })
                : Failure(result.Error, StatusCodes.Status400BadRequest);
        });

        group.MapPost("/indexer-catalog/{sourceId:guid}/{key}/test", async (
            Guid sourceId, string key, CatalogDraftRequest request, IIndexerAdministration admin,
            CancellationToken cancellationToken) =>
        {
            var result = await admin.TestCatalogIndexerAsync(new IndexerCatalogSourceId(sourceId),
                key, request.Name, request.BaseUrl, request.Priority, request.Settings, cancellationToken);
            return result.IsSuccess ? Results.Ok(ToDto(result.Value)) : Failure(result.Error, StatusCodes.Status400BadRequest);
        });

        group.MapPut("/indexers/{id:guid}/settings", async (
            Guid id, IndexerSettings request, IIndexerAdministration admin, CancellationToken cancellationToken) =>
        {
            var result = await admin.SetSettingsAsync(new IndexerId(id), request, cancellationToken);
            return result.IsSuccess ? Results.NoContent() : Failure(result.Error,
                result.Error.Code == "discovery.indexer_not_found"
                    ? StatusCodes.Status404NotFound : StatusCodes.Status400BadRequest);
        });

        group.MapPost("/indexers/{id:guid}/test", async (
            Guid id, IIndexerAdministration admin, CancellationToken cancellationToken) =>
        {
            var result = await admin.TestIndexerAsync(new IndexerId(id), cancellationToken);
            return result.IsSuccess ? Results.Ok(ToDto(result.Value)) : Failure(result.Error, StatusCodes.Status404NotFound);
        });

        group.MapPost("/indexers",
            async (AddIndexerRequest request, IIndexerAdministration admin, CancellationToken cancellationToken) =>
            {
                var definitionId = request.DefinitionId is Guid id ? new IndexerDefinitionId(id) : (IndexerDefinitionId?)null;
                var credential = request.Credential is { } c ? new NewIndexerCredential(c.Secret, c.Username) : null;
                var result = await admin.AddIndexerAsync(
                    request.Name, request.Protocol, request.BaseUrl, request.Priority, definitionId, credential,
                    cancellationToken);
                // CredentialFailure: a missing master key is a 503 here too; everything else stays a 400.
                return result.IsSuccess
                    ? Results.Created($"/api/discovery/indexers/{result.Value}", new { indexerId = result.Value.ToString() })
                    : CredentialFailure(result.Error);
            });

        group.MapGet("/indexers", async (IIndexerAdministration admin, CancellationToken cancellationToken) =>
            Results.Ok((await admin.ListIndexersAsync(cancellationToken)).Select(ToDto)));

        // What the endpoint can be asked. Until this is set the indexer is queried exactly as it was
        // before capabilities existed (no cat=, every standard parameter, both modes assumed).
        group.MapPut("/indexers/{id:guid}/capabilities",
            async (Guid id, SetCapabilitiesRequest request, IIndexerAdministration admin, CancellationToken cancellationToken) =>
            {
                var result = await admin.SetCapabilitiesAsync(new IndexerId(id), request.ToCapabilities(), cancellationToken);
                // A list that does not fit is the caller's to fix (400); only an unknown indexer is a 404.
                return result.IsSuccess ? Results.NoContent() : NotFoundOr(result.Error);
            });

        // Write-only, and the only way to change a credential: there is no GET counterpart, because a
        // stored secret is never readable again through this API. A 503 means this installation has
        // no master key to encrypt with, which is a deployment fact rather than a bad request.
        group.MapPut("/indexers/{id:guid}/credential",
            async (Guid id, SetCredentialRequest request, IIndexerAdministration admin, CancellationToken cancellationToken) =>
            {
                var result = await admin.SetCredentialAsync(
                    new IndexerId(id), request.Username, request.Secret, cancellationToken);
                return result.IsSuccess ? Results.NoContent() : CredentialFailure(result.Error);
            });

        group.MapDelete("/indexers/{id:guid}/credential",
            async (Guid id, IIndexerAdministration admin, CancellationToken cancellationToken) =>
            {
                var result = await admin.ClearCredentialAsync(new IndexerId(id), cancellationToken);
                return result.IsSuccess ? Results.NoContent() : CredentialFailure(result.Error);
            });

        group.MapPut("/indexers/{id:guid}/enabled",
            async (Guid id, SetEnabledRequest request, IIndexerAdministration admin, CancellationToken cancellationToken) =>
            {
                var result = await admin.SetEnabledAsync(new IndexerId(id), request.Enabled, cancellationToken);
                return result.IsSuccess ? Results.NoContent() : NotFoundOr(result.Error);
            });

        group.MapPut("/indexers/{id:guid}/priority",
            async (Guid id, SetPriorityRequest request, IIndexerAdministration admin, CancellationToken cancellationToken) =>
            {
                var result = await admin.SetPriorityAsync(new IndexerId(id), request.Priority, cancellationToken);
                return result.IsSuccess ? Results.NoContent() : NotFoundOr(result.Error);
            });

        group.MapDelete("/indexers/{id:guid}",
            async (Guid id, IIndexerAdministration admin, CancellationToken cancellationToken) =>
            {
                var result = await admin.DeleteIndexerAsync(new IndexerId(id), cancellationToken);
                return result.IsSuccess ? Results.NoContent() : NotFoundOr(result.Error);
            });

        group.MapPost("/indexer-definitions",
            async (UploadDefinitionRequest request, IIndexerAdministration admin, CancellationToken cancellationToken) =>
            {
                var result = await admin.UploadDefinitionAsync(request.Name, request.RawContent, cancellationToken);
                return result.IsSuccess
                    ? Results.Created($"/api/discovery/indexer-definitions/{result.Value}", new { definitionId = result.Value.ToString() })
                    : Results.Json(new { error = result.Error.Code, message = result.Error.Message },
                        statusCode: StatusCodes.Status400BadRequest);
            });

        group.MapGet("/indexer-definitions",
            async (IIndexerAdministration admin, CancellationToken cancellationToken) =>
                Results.Ok((await admin.ListDefinitionsAsync(cancellationToken)).Select(ToDto)));

        // Dry-run only: never issues a real HTTP request, even when a sample response is supplied.
        group.MapPost("/indexer-definitions/validate",
            async (ValidateDefinitionRequest request, IIndexerAdministration admin, CancellationToken cancellationToken) =>
            {
                var result = await admin.ValidateDefinitionAsync(
                    request.RawContent, request.SampleResponseBody, request.SampleRequestUrl, cancellationToken);
                return result.IsSuccess
                    ? Results.Ok(new
                    {
                        candidates = result.Value.Candidates.Select(ToDto),
                        // Every declared rule that produced no value. Without these the body of a
                        // broken definition is indistinguishable from that of a working one.
                        fieldIssues = result.Value.FieldIssues.Select(ToDto),
                    })
                    : Results.Json(new { error = result.Error.Code, message = result.Error.Message },
                        statusCode: StatusCodes.Status400BadRequest);
            });

        // Manual, on-demand search (ops/testing); the SearchRequested flow uses the same service.
        group.MapPost("/search",
            async (SearchRequest request, IReleaseSearch search, CancellationToken cancellationToken) =>
            {
                var criterion = new SearchCriterion(
                    request.Term, request.Year, request.ImdbId, request.TmdbId, request.ContentKind ?? "Movie",
                    request.SeasonNumber, request.EpisodeNumber, TvdbId: request.TvdbId);
                var outcome = await search.SearchAsync(criterion, cancellationToken: cancellationToken);
                return Results.Ok(new
                {
                    executionId = outcome.ExecutionId.ToString(),
                    candidates = outcome.Candidates.Select(ToDto),
                });
            });

        group.MapGet("/executions/{id:guid}/results",
            async (Guid id, IReleaseSearchResults results, CancellationToken cancellationToken) =>
            {
                var candidates = await results.GetResultsAsync(new SearchExecutionId(id), cancellationToken);
                return Results.Ok(candidates.Select(ToDto));
            });

        return endpoints;
    }

    /// <summary>
    /// Catalog source subscriptions. Adding and refreshing answer with the source whatever the fetch
    /// did: a failed fetch or an invalid manifest is recorded on the source (<c>lastRefresh*</c>), not
    /// returned as an error, because the request itself succeeded.
    /// </summary>
    private static void MapCatalogSourceEndpoints(RouteGroupBuilder group)
    {
        group.MapGet("/indexer-catalog/sources", async (
            IIndexerCatalogSources sources, CancellationToken cancellationToken) =>
            Results.Ok((await sources.ListSourcesAsync(cancellationToken)).Select(ToDto)));

        group.MapPost("/indexer-catalog/sources", async (
            AddCatalogSourceRequest request, IIndexerCatalogSources sources, CancellationToken cancellationToken) =>
        {
            var result = await sources.AddSourceAsync(request.Name, request.Url, cancellationToken);
            return result.IsSuccess
                ? Results.Created($"/api/discovery/indexer-catalog/sources/{result.Value.Id}", ToDto(result.Value))
                : CatalogSourceFailure(result.Error);
        });

        group.MapPost("/indexer-catalog/sources/{id:guid}/refresh", async (
            Guid id, IIndexerCatalogSources sources, CancellationToken cancellationToken) =>
        {
            var result = await sources.RefreshSourceAsync(new IndexerCatalogSourceId(id), cancellationToken);
            return result.IsSuccess ? Results.Ok(ToDto(result.Value)) : CatalogSourceFailure(result.Error);
        });

        group.MapPut("/indexer-catalog/sources/{id:guid}", async (
            Guid id, UpdateCatalogSourceRequest request, IIndexerCatalogSources sources,
            CancellationToken cancellationToken) =>
        {
            var result = await sources.UpdateSourceAsync(
                new IndexerCatalogSourceId(id), request.Name, request.Enabled, cancellationToken);
            return result.IsSuccess ? Results.Ok(ToDto(result.Value)) : CatalogSourceFailure(result.Error);
        });

        group.MapDelete("/indexer-catalog/sources/{id:guid}", async (
            Guid id, IIndexerCatalogSources sources, CancellationToken cancellationToken) =>
        {
            var result = await sources.DeleteSourceAsync(new IndexerCatalogSourceId(id), cancellationToken);
            return result.IsSuccess ? Results.NoContent() : CatalogSourceFailure(result.Error);
        });
    }

    private static IResult CatalogSourceFailure(Error error) => Failure(error, error.Code switch
    {
        IndexerCatalogSources.NotFoundCode => StatusCodes.Status404NotFound,
        IndexerCatalogSources.DuplicateUrlCode => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status400BadRequest,
    });

    private static object ToDto(IndexerCatalogSourceSummary source) => new
    {
        id = source.Id.ToString(),
        name = source.Name,
        url = source.Url,
        enabled = source.Enabled,
        createdAt = source.CreatedAt,
        lastRefreshedAt = source.LastRefreshedAt,
        lastRefreshSucceeded = source.LastRefreshSucceeded,
        lastRefreshCode = source.LastRefreshCode,
        lastRefreshMessage = source.LastRefreshMessage,
        entryCount = source.EntryCount,
    };

    /// <summary>
    /// A credential appears here as a username and a state name and nothing else. There is deliberately
    /// no field the secret could occupy, so no future edit can accidentally start populating one, and
    /// the state names a deployment fact — never a value, never key material.
    /// </summary>
    private static object ToDto(IndexerSummary indexer) => new
    {
        id = indexer.Id.ToString(),
        name = indexer.Name,
        protocol = indexer.Protocol.ToString(),
        baseUrl = indexer.BaseUrl,
        priority = indexer.Priority,
        enabled = indexer.Enabled,
        capabilities = ToDto(indexer.Capabilities ?? IndexerCapabilities.Unknown),
        definitionId = indexer.DefinitionId?.ToString(),
        credentialUsername = indexer.CredentialUsername,
        credentialState = indexer.CredentialState.ToString(),
        declaresLogin = indexer.DeclaresLogin,
        sessionState = indexer.SessionState.ToString(),
        lastLoginAt = indexer.LastLoginAt,
        settings = ToDto(indexer.Settings ?? new IndexerSettings()),
        catalogKey = indexer.CatalogKey,
        catalogVersion = indexer.CatalogVersion,
        catalogSourceId = indexer.CatalogSourceId?.ToString(),
        lastTestedAt = indexer.LastTestedAt,
        lastTestSucceeded = indexer.LastTestSucceeded,
        lastTestCode = indexer.LastTestCode,
        lastTestMessage = indexer.LastTestMessage,
    };

    private static object ToDto(IndexerCatalogEntry entry) => new
    {
        key = entry.Key,
        version = entry.Version,
        name = entry.Name,
        description = entry.Description,
        protocol = entry.Protocol.ToString(),
        releaseProtocol = entry.ReleaseProtocol.ToString(),
        baseUrls = entry.BaseUrls,
        requiresFlareSolverr = entry.RequiresFlareSolverr,
        installedIndexerId = entry.InstalledIndexerId?.ToString(),
        defaultPriority = entry.DefaultPriority,
        defaultSettings = ToDto(entry.DefaultSettings),
        sourceId = entry.SourceId.ToString(),
        sourceName = entry.SourceName,
    };

    private static object ToDto(IndexerSettings settings) => new
    {
        minimumSeeders = settings.MinimumSeeders,
        preferMagnet = settings.PreferMagnet,
        queryLimit = settings.QueryLimit,
        grabLimit = settings.GrabLimit,
        limitsUnit = settings.LimitsUnit.ToString(),
        useFlareSolverr = settings.UseFlareSolverr,
    };

    private static object ToDto(IndexerTestResult result) => new
    {
        succeeded = result.Succeeded,
        code = result.Code,
        message = result.Message,
        candidateCount = result.CandidateCount,
        durationMs = result.DurationMs,
        testedAt = result.TestedAt,
        authenticated = result.Authenticated,
    };

    private static IResult Failure(Error error, int statusCode) =>
        Results.Json(new { error = error.Code, message = error.Message }, statusCode: statusCode);

    private static IResult NotFoundOr(Error error) =>
        Failure(error, error.Code == "discovery.indexer_not_found"
            ? StatusCodes.Status404NotFound
            : StatusCodes.Status400BadRequest);

    /// <summary>
    /// An unknown indexer is a 404; a missing master key is a 503, because the request was valid and
    /// the installation is what cannot honour it. Everything else is a 400.
    /// </summary>
    private static IResult CredentialFailure(Error error) => Results.Json(
        new { error = error.Code, message = error.Message },
        statusCode: error.Code switch
        {
            "discovery.indexer_not_found" => StatusCodes.Status404NotFound,
            IndexerCredentialProtector.SecretsUnavailableErrorCode => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status400BadRequest,
        });

    private static object ToDto(IndexerDefinitionSummary definition) => new
    {
        id = definition.Id.ToString(),
        name = definition.Name,
        schemaVersion = definition.SchemaVersion,
        contentHash = definition.ContentHash,
        createdAt = definition.CreatedAt,
    };

    private static object ToDto(IndexerCapabilities capabilities) => new
    {
        supportsMovieSearch = capabilities.SupportsMovieSearch,
        supportsTvSearch = capabilities.SupportsTvSearch,
        movieCategories = capabilities.MovieCategories ?? [],
        tvCategories = capabilities.TvCategories ?? [],
        movieSearchParams = capabilities.MovieSearchParams ?? [],
        tvSearchParams = capabilities.TvSearchParams ?? [],
    };

    private static object ToDto(ReleaseCandidate candidate) => new
    {
        guid = candidate.Guid,
        title = candidate.Title,
        downloadUrl = candidate.DownloadUrl,
        protocol = candidate.Protocol.ToString(),
        sizeBytes = candidate.SizeBytes,
        seeders = candidate.Seeders,
        leechers = candidate.Leechers,
        publishedAt = candidate.PublishedAt,
        indexerName = candidate.IndexerName,
        seasonNumber = candidate.SeasonNumber,
        episodeNumber = candidate.EpisodeNumber,
        tvdbId = candidate.TvdbId,
        category = candidate.Category,
    };

    /// <summary>
    /// A field rule that produced no value, as the dry run reports it. The raw value is the sample
    /// the caller supplied, echoed back truncated — it is their own input, and it is the whole point:
    /// it is how an operator sees that '473K' reached a rule that could not read it.
    /// </summary>
    private static object ToDto(DefinitionFieldIssue issue) => new
    {
        rowIndex = issue.RowIndex,
        field = issue.Field,
        rawValue = issue.RawValue,
        code = issue.Code,
        message = issue.Message,
    };

    /// <summary>
    /// <paramref name="DefinitionId"/> is required for the 'Definition' protocol and rejected otherwise.
    /// <paramref name="Credential"/> is optional and stored in the same save as the indexer.
    /// </summary>
    public sealed record AddIndexerRequest(
        string Name,
        IndexerProtocol Protocol,
        string BaseUrl,
        int Priority,
        Guid? DefinitionId = null,
        SetCredentialRequest? Credential = null);

    public sealed record AddCatalogSourceRequest(string Name, string Url);

    public sealed record UpdateCatalogSourceRequest(string Name, bool Enabled);

    public sealed record CatalogDraftRequest(
        string? Name = null,
        string? BaseUrl = null,
        int? Priority = null,
        IndexerSettings? Settings = null);

    /// <summary>
    /// <paramref name="Username"/> is null for an API key. With one, <paramref name="Secret"/> is a
    /// password: a 'Definition' indexer submits both to its declared login form, and Torznab/Newznab
    /// send them as HTTP Basic (https base URL only). The secret is never echoed back by any route.
    /// </summary>
    public sealed record SetCredentialRequest(string Secret, string? Username = null);

    public sealed record SetEnabledRequest(bool Enabled);

    public sealed record SetPriorityRequest(int Priority);

    public sealed record UploadDefinitionRequest(string Name, string RawContent);

    /// <summary>
    /// <paramref name="SampleResponseBody"/>/<paramref name="SampleRequestUrl"/> are optional: omit
    /// both for format-only validation, or supply both to preview what the definition would extract
    /// from a sample response. Never issues a real HTTP request either way.
    /// </summary>
    public sealed record ValidateDefinitionRequest(
        string RawContent, string? SampleResponseBody = null, string? SampleRequestUrl = null);

    /// <summary>
    /// Manual search request. The series members are trailing optionals so the existing movie shape
    /// posted by any current caller keeps working unchanged.
    /// </summary>
    public sealed record SearchRequest(
        string Term,
        int? Year,
        string? ImdbId,
        string? TmdbId,
        string? ContentKind,
        int? SeasonNumber = null,
        int? EpisodeNumber = null,
        string? TvdbId = null);

    /// <summary>Admin-supplied capabilities. A null list clears the declaration back to "unknown".</summary>
    public sealed record SetCapabilitiesRequest(
        bool SupportsMovieSearch = true,
        bool SupportsTvSearch = true,
        IReadOnlyList<int>? MovieCategories = null,
        IReadOnlyList<int>? TvCategories = null,
        IReadOnlyList<string>? MovieSearchParams = null,
        IReadOnlyList<string>? TvSearchParams = null)
    {
        public IndexerCapabilities ToCapabilities() => new(
            SupportsMovieSearch, SupportsTvSearch, MovieCategories, TvCategories, MovieSearchParams, TvSearchParams);
    }
}
