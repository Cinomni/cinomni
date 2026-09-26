using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Cinomni.Metadata.Contracts;
using Microsoft.Extensions.Logging;

namespace Cinomni.Metadata.Providers;

/// <summary>
/// Production <see cref="IMetadataSource"/> over TheTVDB v4 REST API (movies and series). Authenticates
/// with the bearer JWT owned by <see cref="TvdbTokenProvider"/>, fetches over an SSRF-hardened
/// <see cref="HttpClient"/>, and parses every response defensively (untrusted input).
/// <para>
/// TheTVDB is the only provider publishing an <c>absoluteNumber</c>, which anime resolution depends on,
/// and the only one exposing several season orderings — <see cref="TvdbProviderOptions.SeasonType"/>
/// picks one and it is recorded on the snapshot, because that choice decides which SxxEyy numbers reach
/// the catalog. Episodes come from a paged endpoint, walked up to
/// <see cref="TvdbProviderOptions.MaxEpisodePages"/>.
/// </para>
/// </summary>
public sealed class TvdbMetadataSource(
    HttpClient httpClient,
    TvdbTokenProvider tokenProvider,
    TvdbProviderOptions options,
    ILogger<TvdbMetadataSource> logger) : IMetadataSource
{
    private static readonly IReadOnlySet<MetadataMediaKind> Kinds =
        new HashSet<MetadataMediaKind> { MetadataMediaKind.Movie, MetadataMediaKind.Series };

    // TheTVDB artwork type ids differ by record kind; map the ones we surface to a neutral ArtworkKind.
    private static readonly IReadOnlyDictionary<int, ArtworkKind> MovieArtworkTypes = new Dictionary<int, ArtworkKind>
    {
        [14] = ArtworkKind.Poster, [15] = ArtworkKind.Backdrop, [23] = ArtworkKind.Logo, [25] = ArtworkKind.Logo,
    };

    private static readonly IReadOnlyDictionary<int, ArtworkKind> SeriesArtworkTypes = new Dictionary<int, ArtworkKind>
    {
        [2] = ArtworkKind.Poster, [3] = ArtworkKind.Backdrop, [23] = ArtworkKind.Logo,
    };

    // The bulky arrays of an /extended record: already persisted relationally (artworks) or of no use to
    // the ACL (characters, companies). Dropping them keeps the stored raw_response a debugging aid
    // rather than megabytes of jsonb per refresh.
    private static readonly string[] BulkyRawProperties = ["characters", "artworks", "companies"];

    public string Name => "tvdb";

    public IReadOnlySet<MetadataMediaKind> SupportedKinds => Kinds;

    /// <summary>
    /// No key means no bearer token, so every call would return nothing. Declared here rather than
    /// discovered per call, so a search knows before it starts whether anyone can answer it.
    /// </summary>
    public bool IsAvailable => tokenProvider.HasKey;

    /// <summary>The token provider owns the credential, so it owns the sentence that names its key.</summary>
    public void AnnounceUnavailable()
    {
        if (!IsAvailable)
        {
            tokenProvider.AnnounceDisabledOnce();
        }
    }

    public async Task<IReadOnlyList<ProviderMetadataCandidate>> SearchAsync(MetadataProviderQuery query, CancellationToken cancellationToken = default)
    {
        // Asked up front only to stop early when there is no credential; each request asks again.
        if (await tokenProvider.GetTokenAsync(cancellationToken) is null)
        {
            return [];
        }

        var type = query.Kind == MetadataMediaKind.Series ? "series" : "movie";
        var uri = $"search?query={Uri.EscapeDataString(query.Term)}&type={type}";
        if (query.Year is { } year)
        {
            uri += $"&year={year.ToString(CultureInfo.InvariantCulture)}";
        }

        try
        {
            using var document = await GetJsonAsync(uri, cancellationToken);
            return document is null ? [] : ParseSearch(document.RootElement);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "TVDB search failed for '{Term}'.", query.Term);
            return [];
        }
    }

    public async Task<ProviderMetadataResult?> FetchAsync(string externalId, MetadataMediaKind kind, CancellationToken cancellationToken = default)
    {
        if (await tokenProvider.GetTokenAsync(cancellationToken) is null)
        {
            return null;
        }

        var segment = kind == MetadataMediaKind.Series ? "series" : "movies";
        using var document = await GetJsonAsync($"{segment}/{Uri.EscapeDataString(externalId)}/extended", cancellationToken);
        if (document is null || ProviderJson.Object(document.RootElement, "data") is not { } data)
        {
            return null;
        }

        var series = kind == MetadataMediaKind.Series
            ? await FetchSeriesDetailsAsync(externalId, data, cancellationToken)
            : null;

        return ParseRecord(externalId, kind, data, series);
    }

    private async Task<ProviderSeriesDetails> FetchSeriesDetailsAsync(
        string externalId,
        JsonElement data,
        CancellationToken cancellationToken)
    {
        var seasonType = SeasonOrders.IsKnown(options.SeasonType) ? options.SeasonType.ToLowerInvariant() : SeasonOrders.Official;
        var status = ProviderJson.Object(data, "status") is { } statusElement
            ? ProviderSeriesStatus.Parse(ProviderJson.String(statusElement, "name"))
            : SeriesStatus.Unknown;

        return new ProviderSeriesDetails(
            status,
            ProviderJson.Date(data, "firstAired"),
            ProviderJson.Date(data, "lastAired"),
            seasonType,
            ExternalIdsOf(externalId, data),
            ParseSeasons(data, seasonType),
            await FetchEpisodesAsync(externalId, seasonType, cancellationToken));
    }

    /// <summary>
    /// Walks the paged episode endpoint. <c>links.next</c> is honoured as the "there is more" signal, but
    /// the next request is composed against our own base address rather than by following the returned
    /// absolute URL — a provider response is untrusted input and must never choose the host we call.
    /// </summary>
    private async Task<IReadOnlyList<ProviderEpisode>> FetchEpisodesAsync(
        string externalId,
        string seasonType,
        CancellationToken cancellationToken)
    {
        var episodes = new List<ProviderEpisode>();
        var id = Uri.EscapeDataString(externalId);

        for (var page = 0; page < Math.Max(1, options.MaxEpisodePages); page++)
        {
            JsonDocument? document;
            try
            {
                document = await GetJsonAsync(
                    $"series/{id}/episodes/{seasonType}?page={page.ToString(CultureInfo.InvariantCulture)}",
                    cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
            {
                // A partial structure is still worth persisting; the next refresh retries the rest.
                logger.LogWarning(ex, "TVDB episode page {Page} failed for series {ExternalId}.", page, externalId);
                break;
            }

            if (document is null)
            {
                break;
            }

            using (document)
            {
                if (ProviderJson.Object(document.RootElement, "data") is not { } data)
                {
                    break;
                }

                AppendEpisodes(episodes, data);

                var links = ProviderJson.Object(document.RootElement, "links");
                if (links is null || string.IsNullOrEmpty(ProviderJson.String(links.Value, "next")))
                {
                    break;
                }
            }
        }

        return episodes;
    }

    private static void AppendEpisodes(List<ProviderEpisode> episodes, JsonElement data)
    {
        foreach (var item in ProviderJson.Array(data, "episodes"))
        {
            // (season, number) is the natural key: an entry missing either cannot be addressed by SxxEyy.
            if (ProviderJson.Int(item, "seasonNumber") is not { } season || ProviderJson.Int(item, "number") is not { } number)
            {
                continue;
            }

            episodes.Add(new ProviderEpisode(
                season,
                number,
                ProviderJson.String(item, "name") ?? $"Episode {number.ToString(CultureInfo.InvariantCulture)}",
                ProviderJson.String(item, "overview"),
                ProviderJson.Int(item, "absoluteNumber"),
                ProviderJson.Date(item, "aired"),
                // "aired" is date-only — inventing midnight UTC here would fabricate a broadcast time.
                AirDateTime: null,
                ProviderJson.Int(item, "runtime"),
                ProviderJson.String(item, "image"),
                ProviderJson.Int(item, "id")?.ToString(CultureInfo.InvariantCulture),
                IsSpecial: season == 0));
        }
    }

    // data.seasons[] lists every ordering the series has; only the configured one defines our numbering.
    private static IReadOnlyList<ProviderSeason> ParseSeasons(JsonElement data, string seasonType)
    {
        var seasons = new List<ProviderSeason>();
        foreach (var item in ProviderJson.Array(data, "seasons"))
        {
            if (ProviderJson.Int(item, "number") is not { } number)
            {
                continue;
            }

            var type = ProviderJson.Object(item, "type") is { } typeElement ? ProviderJson.String(typeElement, "type") : null;
            if (type is not null && !type.Equals(seasonType, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            seasons.Add(new ProviderSeason(
                number,
                ProviderJson.String(item, "name"),
                ProviderJson.String(item, "overview"),
                EpisodeCount: null,
                ProviderJson.Date(item, "firstAired"),
                ProviderJson.String(item, "image"),
                ProviderJson.Int(item, "id")?.ToString(CultureInfo.InvariantCulture)));
        }

        return seasons;
    }

    // remoteIds: [{ id, type, sourceName }]. sourceName is the stable signal; the numeric type is the
    // documented fallback (2 = IMDB, 12 = TheMovieDB).
    private static ProviderExternalIds ExternalIdsOf(string externalId, JsonElement data)
    {
        string? imdbId = null;
        string? tmdbId = null;

        foreach (var item in ProviderJson.Array(data, "remoteIds"))
        {
            var value = ProviderJson.String(item, "id");
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            var source = ProviderJson.String(item, "sourceName") ?? string.Empty;
            var type = ProviderJson.Int(item, "type");
            if (source.Contains("imdb", StringComparison.OrdinalIgnoreCase) || type == 2)
            {
                imdbId ??= value;
            }
            else if (source.Contains("moviedb", StringComparison.OrdinalIgnoreCase) || type == 12)
            {
                tmdbId ??= value;
            }
        }

        return new ProviderExternalIds(externalId, imdbId, tmdbId);
    }

    /// <summary>
    /// One authenticated GET. The token is asked for here, per request, so one renewed mid-series
    /// reaches every page after it. A 401 means the cached token is no longer accepted: it is
    /// forgotten and the request is sent once more with a fresh one, so a revoked token costs one extra
    /// login rather than every call until its assumed lifetime ends.
    /// </summary>
    private async Task<JsonDocument?> GetJsonAsync(string uri, CancellationToken cancellationToken)
    {
        if (await tokenProvider.GetTokenAsync(cancellationToken) is not { } token)
        {
            return null;
        }

        using var response = await SendAsync(uri, token, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            tokenProvider.Invalidate(token);
            if (await tokenProvider.GetTokenAsync(cancellationToken) is { } fresh)
            {
                using var retried = await SendAsync(uri, fresh, cancellationToken);
                return await ReadJsonAsync(retried, cancellationToken);
            }
        }

        return await ReadJsonAsync(response, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(string uri, string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await httpClient.SendAsync(request, cancellationToken);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        response.EnsureSuccessStatusCode();
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonDocument.Parse(raw);
    }

    private static IReadOnlyList<ProviderMetadataCandidate> ParseSearch(JsonElement root)
    {
        var candidates = new List<ProviderMetadataCandidate>();
        foreach (var item in ProviderJson.Array(root, "data"))
        {
            var id = ProviderJson.String(item, "tvdb_id") ?? ProviderJson.String(item, "id");
            var title = ProviderJson.String(item, "name");
            if (id is null || title is null)
            {
                continue;
            }

            candidates.Add(new ProviderMetadataCandidate(
                id,
                title,
                ProviderJson.Year(ProviderJson.String(item, "year")),
                ProviderJson.String(item, "overview"),
                SearchExternalIdsOf(id, item)));
        }

        return candidates;
    }

    // A search hit publishes its cross-references as a flat object (remote_ids), unlike the extended
    // record's array — both shapes are read so a candidate can be merged across providers.
    private static ProviderExternalIds SearchExternalIdsOf(string id, JsonElement item)
    {
        if (ProviderJson.Object(item, "remote_ids") is { } flat)
        {
            return new ProviderExternalIds(id, ProviderJson.String(flat, "imdb_id"), ProviderJson.String(flat, "tmdb_id"));
        }

        string? imdbId = null;
        string? tmdbId = null;
        foreach (var remote in ProviderJson.Array(item, "remote_ids"))
        {
            var value = ProviderJson.String(remote, "id");
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            var source = ProviderJson.String(remote, "sourceName") ?? string.Empty;
            if (source.Contains("imdb", StringComparison.OrdinalIgnoreCase))
            {
                imdbId ??= value;
            }
            else if (source.Contains("moviedb", StringComparison.OrdinalIgnoreCase))
            {
                tmdbId ??= value;
            }
        }

        return new ProviderExternalIds(id, imdbId, tmdbId);
    }

    private static ProviderMetadataResult ParseRecord(
        string externalId,
        MetadataMediaKind kind,
        JsonElement data,
        ProviderSeriesDetails? series)
    {
        var title = ProviderJson.String(data, "name") ?? externalId;
        var artwork = ParseArtwork(kind, data);
        // A TheTVDB movie record exposes "runtime"; a series record exposes "averageRuntime" (there is no
        // series-level "runtime"), so pick the field by kind or the series runtime is always null.
        var runtime = kind == MetadataMediaKind.Series
            ? ProviderJson.Int(data, "averageRuntime") ?? ProviderJson.Int(data, "runtime")
            : ProviderJson.Int(data, "runtime");
        return new ProviderMetadataResult(
            externalId,
            title,
            null,
            ProviderJson.Year(ProviderJson.String(data, "year")),
            ProviderJson.String(data, "overview"),
            runtime,
            ProviderLanguage.Normalize(ProviderJson.String(data, "originalLanguage")),
            FirstUrl(artwork, ArtworkKind.Poster),
            FirstUrl(artwork, ArtworkKind.Backdrop),
            ProviderJson.RawWithout(data, BulkyRawProperties),
            artwork,
            series);
    }

    private static IReadOnlyList<ProviderArtwork> ParseArtwork(MetadataMediaKind kind, JsonElement data)
    {
        var artwork = new List<ProviderArtwork>();
        var typeMap = kind == MetadataMediaKind.Series ? SeriesArtworkTypes : MovieArtworkTypes;
        foreach (var item in ProviderJson.Array(data, "artworks"))
        {
            var url = ProviderJson.String(item, "image");
            var type = ProviderJson.Int(item, "type");
            if (url is null || type is null || !typeMap.TryGetValue(type.Value, out var artworkKind))
            {
                continue;
            }

            artwork.Add(new ProviderArtwork(
                artworkKind,
                url,
                ProviderLanguage.Normalize(ProviderJson.String(item, "language")),
                ProviderJson.Int(item, "width"),
                ProviderJson.Int(item, "height"),
                VoteAverage: null,
                VoteCount: ProviderJson.Int(item, "score")));
        }

        return artwork;
    }

    private static string? FirstUrl(IReadOnlyList<ProviderArtwork> artwork, ArtworkKind kind) =>
        artwork.FirstOrDefault(a => a.Kind == kind)?.Url;
}
