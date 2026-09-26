using System.Globalization;
using System.Text.Json;
using Cinomni.Metadata.Contracts;
using Cinomni.Metadata.Application;
using Cinomni.Operations.Settings;
using Microsoft.Extensions.Logging;

namespace Cinomni.Metadata.Providers;

/// <summary>
/// Production <see cref="IMetadataSource"/> over the TMDB REST API. Uses an SSRF-hardened
/// <see cref="HttpClient"/> (configured at registration), sends the referenced API key as a query
/// parameter, and treats every response as untrusted (defensive JSON parsing, no <c>eval</c>).
/// <para>
/// Covers movies (<c>movie/{id}</c>) and series (<c>tv/{id}</c>); the full artwork set arrives in one call
/// via <c>append_to_response=images</c>. TMDB does not embed episodes, so a series structure costs one
/// request per season — bounded by <see cref="TmdbProviderOptions.MaxSeasonRequests"/> so a
/// hundred-season record cannot turn one refresh into a hundred round trips.
/// </para>
/// </summary>
public sealed class TmdbMetadataSource(
    HttpClient httpClient,
    TmdbProviderOptions options,
    DisabledProviderNotice notice,
    ILogger<TmdbMetadataSource> logger,
    // Optional, like every other dependency a composition may legitimately not have: a service
    // collection that registers the adapters without the settings store has no region to read, and
    // that is the same state as an installation which has named none. The feature degrades to "no
    // classification", which it already has to do correctly anyway.
    ILiveOptions<ContentRatingOptions>? contentRating = null,
    // The key as the settings store resolves it now, so one saved from the console applies at once.
    // Optional for the same reason as the region: a composition with no store reads the composed key.
    ILiveOptions<MetadataProviderKeys>? keys = null) : IMetadataSource
{
    /// <summary>The one key that switches this provider on; named in the log line, never its value.</summary>
    private const string ApiKeyConfigurationKey = "Metadata:Tmdb:ApiKey";

    private static readonly IReadOnlySet<MetadataMediaKind> Kinds =
        new HashSet<MetadataMediaKind> { MetadataMediaKind.Movie, MetadataMediaKind.Series };

    public string Name => "tmdb";

    public IReadOnlySet<MetadataMediaKind> SupportedKinds => Kinds;

    /// <summary>TMDB answers nothing without a key, and a key-less request would only earn a 401.</summary>
    public bool IsAvailable => !string.IsNullOrEmpty(ApiKey);

    /// <summary>Read per use, never cached: an administrator may replace it between two requests.</summary>
    private string ApiKey => MetadataProviderKeys.Effective(keys?.Current.TmdbApiKey ?? string.Empty, options.ApiKey);

    /// <summary>
    /// The line an installation with no key needs, in the same register TheTVDB already uses. It matters
    /// more here than there: TMDB is the only provider that covers a movie, so without it the whole
    /// "add a movie" flow — the first thing anyone does — returns nothing at all.
    /// </summary>
    public void AnnounceUnavailable()
    {
        if (IsAvailable)
        {
            return;
        }

        notice.AnnounceOnce(
            "TMDB",
            ApiKeyConfigurationKey,
            "It is the only provider that covers movies, so movie searches and refreshes find nothing at "
            + "all until a key is configured.");
    }

    public async Task<IReadOnlyList<ProviderMetadataCandidate>> SearchAsync(MetadataProviderQuery query, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            AnnounceUnavailable();
            return [];
        }

        var isSeries = query.Kind == MetadataMediaKind.Series;
        var uri = $"search/{(isSeries ? "tv" : "movie")}?{Credentials()}&query={Uri.EscapeDataString(query.Term)}";
        if (query.Year is { } year)
        {
            var parameter = isSeries ? "first_air_date_year" : "year";
            uri += $"&{parameter}={year.ToString(CultureInfo.InvariantCulture)}";
        }

        try
        {
            using var response = await httpClient.GetAsync(uri, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return ParseSearch(document.RootElement, isSeries);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "TMDB search failed for '{Term}'.", query.Term);
            return [];
        }
    }

    /// <summary>
    /// The weekly trending document for <paramref name="kind"/>, or null when this provider cannot
    /// answer. The caller owns the document and must dispose it. Parsing stays outside this method so
    /// a hostile payload can be tested without a socket.
    /// </summary>
    public async Task<JsonDocument?> GetTrendingDocumentAsync(MetadataMediaKind kind, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable || kind is not (MetadataMediaKind.Movie or MetadataMediaKind.Series))
        {
            return null;
        }

        var segment = kind == MetadataMediaKind.Series ? "tv" : "movie";
        using var response = await httpClient.GetAsync($"trending/{segment}/week?{Credentials()}", cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    public async Task<ProviderMetadataResult?> FetchAsync(string externalId, MetadataMediaKind kind, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            AnnounceUnavailable();
            return null;
        }

        var isSeries = kind == MetadataMediaKind.Series;
        var segment = isSeries ? "tv" : "movie";
        var id = Uri.EscapeDataString(externalId);
        // TMDB keeps classifications in their own sub-resource, one per kind, and appending is free:
        // it is the same request either way, so an installation that has named no region simply reads
        // a block it then ignores rather than paying for a second round trip when it has.
        var appended = isSeries ? "images,external_ids,content_ratings" : "images,release_dates";
        var uri = $"{segment}/{id}?{Credentials()}&append_to_response={appended}&include_image_language={Uri.EscapeDataString(ImageLanguageQuery())}";

        using var response = await httpClient.GetAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;

        return isSeries
            ? ParseRecord(externalId, root, raw, "name", "original_name", "first_air_date", await FetchSeriesDetailsAsync(externalId, root, cancellationToken))
            : ParseRecord(externalId, root, raw, "title", "original_title", "release_date", series: null);
    }

    private async Task<ProviderSeriesDetails> FetchSeriesDetailsAsync(string externalId, JsonElement show, CancellationToken cancellationToken)
    {
        var seasons = ParseSeasons(show);
        var episodes = new List<ProviderEpisode>();

        // One request per season, oldest first, capped: the cap is what keeps a pathological record from
        // turning a single refresh into an unbounded fan-out against a rate-limited provider.
        foreach (var season in seasons.Take(Math.Max(1, options.MaxSeasonRequests)))
        {
            await AppendSeasonEpisodesAsync(externalId, season.Number, episodes, cancellationToken);
        }

        var externalIds = ProviderJson.Object(show, "external_ids");
        return new ProviderSeriesDetails(
            ProviderSeriesStatus.Parse(ProviderJson.String(show, "status")),
            ProviderJson.Date(show, "first_air_date"),
            ProviderJson.Date(show, "last_air_date"),
            // TMDB publishes a single ordering — there is no dvd/absolute alternative to choose from.
            SeasonOrders.Official,
            new ProviderExternalIds(
                externalIds is null ? null : ProviderJson.Int(externalIds.Value, "tvdb_id")?.ToString(CultureInfo.InvariantCulture),
                externalIds is null ? null : ProviderJson.String(externalIds.Value, "imdb_id"),
                externalId),
            seasons,
            episodes);
    }

    private async Task AppendSeasonEpisodesAsync(
        string externalId,
        int seasonNumber,
        List<ProviderEpisode> episodes,
        CancellationToken cancellationToken)
    {
        var uri = $"tv/{Uri.EscapeDataString(externalId)}/season/{seasonNumber.ToString(CultureInfo.InvariantCulture)}?{Credentials()}";
        try
        {
            using var response = await httpClient.GetAsync(uri, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            AppendEpisodes(episodes, document.RootElement, seasonNumber);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            // A partial structure is still worth persisting; the next refresh retries the missing season.
            logger.LogWarning(ex, "TMDB season {Season} fetch failed for series {ExternalId}.", seasonNumber, externalId);
        }
    }

    private void AppendEpisodes(List<ProviderEpisode> episodes, JsonElement season, int seasonNumber)
    {
        foreach (var item in ProviderJson.Array(season, "episodes"))
        {
            if (ProviderJson.Int(item, "episode_number") is not { } number)
            {
                continue;
            }

            var season0 = ProviderJson.Int(item, "season_number") ?? seasonNumber;
            episodes.Add(new ProviderEpisode(
                season0,
                number,
                ProviderJson.String(item, "name") ?? $"Episode {number.ToString(CultureInfo.InvariantCulture)}",
                ProviderJson.String(item, "overview"),
                // TMDB publishes no absolute numbering; TheTVDB is the only source for it.
                AbsoluteNumber: null,
                ProviderJson.Date(item, "air_date"),
                // "air_date" is date-only — inventing midnight UTC would fabricate a broadcast time.
                AirDateTime: null,
                ProviderJson.Int(item, "runtime"),
                ImageUrl(ProviderJson.String(item, "still_path")),
                ProviderJson.Int(item, "id")?.ToString(CultureInfo.InvariantCulture),
                IsSpecial: season0 == 0));
        }
    }

    private IReadOnlyList<ProviderSeason> ParseSeasons(JsonElement show)
    {
        var seasons = new List<ProviderSeason>();
        foreach (var item in ProviderJson.Array(show, "seasons"))
        {
            if (ProviderJson.Int(item, "season_number") is not { } number)
            {
                continue;
            }

            seasons.Add(new ProviderSeason(
                number,
                ProviderJson.String(item, "name"),
                ProviderJson.String(item, "overview"),
                ProviderJson.Int(item, "episode_count"),
                ProviderJson.Date(item, "air_date"),
                ImageUrl(ProviderJson.String(item, "poster_path")),
                ProviderJson.Int(item, "id")?.ToString(CultureInfo.InvariantCulture)));
        }

        return seasons.OrderBy(s => s.Number).ToList();
    }

    private string Credentials() =>
        $"api_key={Uri.EscapeDataString(ApiKey)}&language={Uri.EscapeDataString(options.Language)}";

    private string ImageLanguageQuery()
    {
        // Ask TMDB for the preferred language, plus text-less images (null) and English as fallbacks.
        var language = LanguageOf(options.Language);
        return language is "en" or "" ? "en,null" : $"{language},en,null";
    }

    private IReadOnlyList<ProviderMetadataCandidate> ParseSearch(JsonElement root, bool isSeries)
    {
        var titleProperty = isSeries ? "name" : "title";
        var originalTitleProperty = isSeries ? "original_name" : "original_title";
        var dateProperty = isSeries ? "first_air_date" : "release_date";

        var candidates = new List<ProviderMetadataCandidate>();
        foreach (var item in ProviderJson.Array(root, "results"))
        {
            var id = ProviderJson.Int(item, "id");
            var title = ProviderJson.String(item, titleProperty) ?? ProviderJson.String(item, originalTitleProperty);
            if (id is null || title is null)
            {
                continue;
            }

            // A TMDB search hit publishes no cross-references, but its own id is one: it is what lets a
            // TheTVDB hit carrying remoteIds[TheMovieDB] be recognised as the same show.
            var externalId = id.Value.ToString(CultureInfo.InvariantCulture);
            candidates.Add(new ProviderMetadataCandidate(
                externalId,
                title,
                ProviderJson.Year(ProviderJson.String(item, dateProperty)),
                ProviderJson.String(item, "overview"),
                new ProviderExternalIds(TmdbId: externalId)));
        }

        return candidates;
    }

    private ProviderMetadataResult ParseRecord(
        string externalId,
        JsonElement record,
        string raw,
        string titleProperty,
        string originalTitleProperty,
        string dateProperty,
        ProviderSeriesDetails? series)
    {
        var title = ProviderJson.String(record, titleProperty) ?? ProviderJson.String(record, originalTitleProperty) ?? externalId;
        var posterUrl = ImageUrl(ProviderJson.String(record, "poster_path"));
        var backdropUrl = ImageUrl(ProviderJson.String(record, "backdrop_path"));
        return new ProviderMetadataResult(
            externalId,
            title,
            ProviderJson.String(record, originalTitleProperty),
            ProviderJson.Year(ProviderJson.String(record, dateProperty)),
            ProviderJson.String(record, "overview"),
            RuntimeOf(record),
            ProviderJson.String(record, "original_language"),
            posterUrl,
            backdropUrl,
            raw,
            ParseArtwork(record, posterUrl, backdropUrl),
            series,
            ParseGenres(record),
            ParseContentRating(record, series is not null));
    }

    /// <summary>The genre names TMDB publishes, in its order, deduplicated and never empty strings.</summary>
    private static IReadOnlyList<string> ParseGenres(JsonElement record) =>
        [.. ProviderJson.Array(record, "genres")
            .Select(genre => ProviderJson.String(genre, "name"))
            .OfType<string>()
            .Select(name => name.Trim())
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// The classification for the configured region, or null.
    /// <para>
    /// Null when no region is configured, when the provider lists none for it, or when the entry it
    /// does list is blank — TMDB routinely returns a country with an empty certification, which is
    /// the provider saying "we do not know" and must not be stored as though it were a rating.
    /// </para>
    /// <para>
    /// The two kinds are shaped differently and there is no way around reading both: a series carries
    /// <c>content_ratings.results[].rating</c>, while a movie carries
    /// <c>release_dates.results[].release_dates[].certification</c> — a list per country, because a
    /// film can be certified once for cinema and again for its home release.
    /// </para>
    /// </summary>
    private string? ParseContentRating(JsonElement record, bool isSeries)
    {
        if (contentRating?.Current is not { IsConfigured: true } region)
        {
            return null;
        }

        // Both blocks are objects wrapping a "results" array, not arrays themselves — the shape
        // append_to_response gives back, and the one the fixtures are written from.
        if (ProviderJson.Object(record, isSeries ? "content_ratings" : "release_dates") is not { } block)
        {
            return null;
        }

        var results = ProviderJson.Array(block, "results")
            .Where(entry => string.Equals(
                ProviderJson.String(entry, "iso_3166_1"), region.Region, StringComparison.OrdinalIgnoreCase));

        foreach (var entry in results)
        {
            var rating = isSeries
                ? ProviderJson.String(entry, "rating")
                : ProviderJson.Array(entry, "release_dates")
                    .Select(release => ProviderJson.String(release, "certification"))
                    .FirstOrDefault(certification => !string.IsNullOrWhiteSpace(certification));

            if (!string.IsNullOrWhiteSpace(rating))
            {
                return rating.Trim();
            }
        }

        return null;
    }

    // A movie carries a scalar "runtime"; a series carries "episode_run_time": [60, 62].
    private static int? RuntimeOf(JsonElement record) =>
        ProviderJson.Int(record, "runtime")
        ?? ProviderJson.Array(record, "episode_run_time")
            .Select(e => e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var minutes) ? minutes : (int?)null)
            .FirstOrDefault(minutes => minutes is not null);

    private IReadOnlyList<ProviderArtwork> ParseArtwork(JsonElement record, string? defaultPoster, string? defaultBackdrop)
    {
        var artwork = new List<ProviderArtwork>();
        if (ProviderJson.Object(record, "images") is { } images)
        {
            AppendImages(artwork, images, "posters", ArtworkKind.Poster);
            AppendImages(artwork, images, "backdrops", ArtworkKind.Backdrop);
            AppendImages(artwork, images, "logos", ArtworkKind.Logo);
        }

        // Fall back to the record's own default artwork if the images block was empty/absent.
        if (!artwork.Any(a => a.Kind == ArtworkKind.Poster) && defaultPoster is not null)
        {
            artwork.Add(new ProviderArtwork(ArtworkKind.Poster, defaultPoster, null, null, null, null, null));
        }

        if (!artwork.Any(a => a.Kind == ArtworkKind.Backdrop) && defaultBackdrop is not null)
        {
            artwork.Add(new ProviderArtwork(ArtworkKind.Backdrop, defaultBackdrop, null, null, null, null, null));
        }

        return artwork;
    }

    private void AppendImages(List<ProviderArtwork> artwork, JsonElement images, string property, ArtworkKind kind)
    {
        foreach (var image in ProviderJson.Array(images, property))
        {
            var url = ImageUrl(ProviderJson.String(image, "file_path"));
            if (url is null)
            {
                continue;
            }

            artwork.Add(new ProviderArtwork(
                kind,
                url,
                NormalizeLanguage(ProviderJson.String(image, "iso_639_1")),
                ProviderJson.Int(image, "width"),
                ProviderJson.Int(image, "height"),
                ProviderJson.Double(image, "vote_average"),
                ProviderJson.Int(image, "vote_count")));
        }
    }

    private string? ImageUrl(string? path) =>
        string.IsNullOrEmpty(path) ? null : $"{options.ImageBaseAddress.TrimEnd('/')}/{path.TrimStart('/')}";

    // TMDB uses "" / null for text-less images; keep null so the selector's language rules see them as neutral.
    private static string? NormalizeLanguage(string? language) => string.IsNullOrEmpty(language) ? null : language;

    private static string LanguageOf(string bcp47) =>
        bcp47.Split('-', StringSplitOptions.RemoveEmptyEntries) is [var primary, ..] ? primary : bcp47;
}
