using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cinomni.Metadata.Contracts;
using Microsoft.Extensions.Logging;

namespace Cinomni.Metadata.Providers;

/// <summary>
/// Production <see cref="IMetadataSource"/> over the TVMaze REST API (series only — TVMaze holds no
/// movies). No credentials required. Fetches over an SSRF-hardened <see cref="HttpClient"/> and parses
/// defensively.
/// <para>
/// The show is fetched with <c>embed[]=episodes&amp;embed[]=seasons</c>, so one request carries the whole
/// structure. TVMaze is the only provider publishing a genuinely timezone-aware air time
/// (<c>airstamp</c>), which is why <see cref="ProviderEpisode.AirDateTime"/> is populated here and nowhere
/// else. The embedded arrays are stripped from the raw response before it is
/// returned: they are persisted relationally and a long-running show's embed is megabytes of jsonb.
/// </para>
/// </summary>
public sealed partial class TvMazeMetadataSource(
    HttpClient httpClient,
    ILogger<TvMazeMetadataSource> logger) : IMetadataSource
{
    private static readonly IReadOnlySet<MetadataMediaKind> Kinds = new HashSet<MetadataMediaKind> { MetadataMediaKind.Series };

    // TVMaze image taxonomy: poster / background / banner / typography. The title-logo treatment is
    // "typography" (a "banner" is a wide strip, not a logo), so map that to Logo and leave banner out.
    private static readonly IReadOnlyDictionary<string, ArtworkKind> ArtworkTypes = new Dictionary<string, ArtworkKind>(StringComparer.OrdinalIgnoreCase)
    {
        ["poster"] = ArtworkKind.Poster,
        ["background"] = ArtworkKind.Backdrop,
        ["typography"] = ArtworkKind.Logo,
    };

    public string Name => "tvmaze";

    public IReadOnlySet<MetadataMediaKind> SupportedKinds => Kinds;

    public async Task<IReadOnlyList<ProviderMetadataCandidate>> SearchAsync(MetadataProviderQuery query, CancellationToken cancellationToken = default)
    {
        if (query.Kind != MetadataMediaKind.Series)
        {
            return [];
        }

        try
        {
            using var document = await GetJsonAsync($"search/shows?q={Uri.EscapeDataString(query.Term)}", cancellationToken);
            return document is null ? [] : ParseSearch(document.RootElement, query.Year);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "TVMaze search failed for '{Term}'.", query.Term);
            return [];
        }
    }

    public async Task<ProviderMetadataResult?> FetchAsync(string externalId, MetadataMediaKind kind, CancellationToken cancellationToken = default)
    {
        if (kind != MetadataMediaKind.Series)
        {
            return null;
        }

        var id = Uri.EscapeDataString(externalId);
        using var show = await GetJsonAsync($"shows/{id}?embed[]=episodes&embed[]=seasons", cancellationToken);
        if (show is null)
        {
            return null;
        }

        // Artwork lives on a separate endpoint; a failure there must not sink the snapshot.
        IReadOnlyList<ProviderArtwork> artwork = [];
        try
        {
            using var images = await GetJsonAsync($"shows/{id}/images", cancellationToken);
            if (images is not null)
            {
                artwork = ParseArtwork(images.RootElement);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "TVMaze image fetch failed for show {ExternalId}.", externalId);
        }

        return ParseShow(externalId, show.RootElement, artwork);
    }

    private async Task<JsonDocument?> GetJsonAsync(string uri, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonDocument.Parse(raw);
    }

    private static IReadOnlyList<ProviderMetadataCandidate> ParseSearch(JsonElement root, int? year)
    {
        var candidates = new List<ProviderMetadataCandidate>();
        if (root.ValueKind != JsonValueKind.Array)
        {
            return candidates;
        }

        foreach (var item in root.EnumerateArray())
        {
            if (ProviderJson.Object(item, "show") is not { } show)
            {
                continue;
            }

            var id = ProviderJson.Int(show, "id");
            var title = ProviderJson.String(show, "name");
            if (id is null || title is null)
            {
                continue;
            }

            var showYear = ProviderJson.Year(ProviderJson.String(show, "premiered"));
            if (year is not null && showYear is not null && showYear != year)
            {
                continue;
            }

            candidates.Add(new ProviderMetadataCandidate(
                id.Value.ToString(CultureInfo.InvariantCulture),
                title,
                showYear,
                StripHtml(ProviderJson.String(show, "summary")),
                ExternalIdsOf(show)));
        }

        return candidates;
    }

    // externals: { "thetvdb": 121361, "imdb": "tt0944947", "tvrage": 24493 }. TheTVDB's id is numeric
    // here and a string everywhere else, so it is normalised to text at the boundary.
    private static ProviderExternalIds ExternalIdsOf(JsonElement show)
    {
        if (ProviderJson.Object(show, "externals") is not { } externals)
        {
            return ProviderExternalIds.None;
        }

        var tvdbId = ProviderJson.Int(externals, "thetvdb")?.ToString(CultureInfo.InvariantCulture);
        return new ProviderExternalIds(tvdbId, ProviderJson.String(externals, "imdb"));
    }

    private static ProviderMetadataResult ParseShow(string externalId, JsonElement show, IReadOnlyList<ProviderArtwork> artwork)
    {
        var title = ProviderJson.String(show, "name") ?? externalId;
        var runtime = ProviderJson.Int(show, "averageRuntime") ?? ProviderJson.Int(show, "runtime");
        var embedded = ProviderJson.Object(show, "_embedded");

        var series = new ProviderSeriesDetails(
            ProviderSeriesStatus.Parse(ProviderJson.String(show, "status")),
            ProviderJson.Date(show, "premiered"),
            ProviderJson.Date(show, "ended"),
            // TVMaze publishes a single, official ordering — there is no dvd/absolute alternative.
            SeasonOrders.Official,
            ExternalIdsOf(show),
            embedded is null ? [] : ParseSeasons(embedded.Value),
            embedded is null ? [] : ParseEpisodes(embedded.Value));

        return new ProviderMetadataResult(
            externalId,
            title,
            null,
            ProviderJson.Year(ProviderJson.String(show, "premiered")),
            StripHtml(ProviderJson.String(show, "summary")),
            runtime,
            ProviderLanguage.Normalize(ProviderJson.String(show, "language")),
            FirstUrl(artwork, ArtworkKind.Poster) ?? ImageOf(show),
            FirstUrl(artwork, ArtworkKind.Backdrop),
            // The embedded arrays are already persisted relationally — keeping them in the jsonb blob
            // would store the whole episode list a second time, per provider, per refresh.
            ProviderJson.RawWithout(show, "_embedded"),
            artwork,
            series);
    }

    private static IReadOnlyList<ProviderSeason> ParseSeasons(JsonElement embedded)
    {
        var seasons = new List<ProviderSeason>();
        foreach (var item in ProviderJson.Array(embedded, "seasons"))
        {
            if (ProviderJson.Int(item, "number") is not { } number)
            {
                continue;
            }

            seasons.Add(new ProviderSeason(
                number,
                NullIfEmpty(ProviderJson.String(item, "name")),
                StripHtml(ProviderJson.String(item, "summary")),
                ProviderJson.Int(item, "episodeOrder"),
                ProviderJson.Date(item, "premiereDate"),
                ImageOf(item),
                ProviderJson.Int(item, "id")?.ToString(CultureInfo.InvariantCulture)));
        }

        return seasons;
    }

    private static IReadOnlyList<ProviderEpisode> ParseEpisodes(JsonElement embedded)
    {
        var episodes = new List<ProviderEpisode>();
        foreach (var item in ProviderJson.Array(embedded, "episodes"))
        {
            // (season, number) is the natural key: an entry missing either cannot be addressed by
            // SxxEyy, so it is dropped rather than guessed at.
            if (ProviderJson.Int(item, "season") is not { } season || ProviderJson.Int(item, "number") is not { } number)
            {
                continue;
            }

            episodes.Add(new ProviderEpisode(
                season,
                number,
                ProviderJson.String(item, "name") ?? $"Episode {number.ToString(CultureInfo.InvariantCulture)}",
                StripHtml(ProviderJson.String(item, "summary")),
                // TVMaze publishes no absolute numbering; TheTVDB is the only source for it.
                AbsoluteNumber: null,
                ProviderJson.Date(item, "airdate"),
                // airstamp is the one genuinely timezone-aware air time any provider gives us.
                ProviderJson.Instant(item, "airstamp"),
                ProviderJson.Int(item, "runtime"),
                ImageOf(item),
                ProviderJson.Int(item, "id")?.ToString(CultureInfo.InvariantCulture),
                IsSpecial(item, season)));
        }

        return episodes;
    }

    // TVMaze types an entry as regular | significant_special | insignificant_special. Season 0 is the
    // specials bucket even when the type is absent, so both signals are honoured.
    private static bool IsSpecial(JsonElement episode, int season) =>
        season == 0
        || ProviderJson.String(episode, "type")?.Contains("special", StringComparison.OrdinalIgnoreCase) == true;

    private static IReadOnlyList<ProviderArtwork> ParseArtwork(JsonElement root)
    {
        var artwork = new List<ProviderArtwork>();
        if (root.ValueKind != JsonValueKind.Array)
        {
            return artwork;
        }

        foreach (var item in root.EnumerateArray())
        {
            var type = ProviderJson.String(item, "type");
            if (type is null || !ArtworkTypes.TryGetValue(type, out var artworkKind))
            {
                continue;
            }

            if (ProviderJson.Object(item, "resolutions") is not { } resolutions
                || ProviderJson.Object(resolutions, "original") is not { } original)
            {
                continue;
            }

            var url = ProviderJson.String(original, "url");
            if (url is null)
            {
                continue;
            }

            artwork.Add(new ProviderArtwork(
                artworkKind,
                url,
                Language: null,
                ProviderJson.Int(original, "width"),
                ProviderJson.Int(original, "height"),
                VoteAverage: null,
                VoteCount: null));
        }

        return artwork;
    }

    private static string? ImageOf(JsonElement element) =>
        ProviderJson.Object(element, "image") is { } image
            ? ProviderJson.String(image, "original") ?? ProviderJson.String(image, "medium")
            : null;

    private static string? FirstUrl(IReadOnlyList<ProviderArtwork> artwork, ArtworkKind kind) =>
        artwork.FirstOrDefault(a => a.Kind == kind)?.Url;

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    // TVMaze summaries are small HTML fragments; strip tags to a plain neutral overview. Bounded,
    // non-backtracking pattern with a timeout (anti-ReDoS, matching the release-parser convention).
    private static string? StripHtml(string? html) =>
        string.IsNullOrEmpty(html) ? html : HtmlTag().Replace(html, string.Empty).Trim();

    [GeneratedRegex("<[^>]{0,200}>", RegexOptions.None, matchTimeoutMilliseconds: 200)]
    private static partial Regex HtmlTag();
}
