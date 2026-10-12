using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Cinomni.Subtitles.Contracts;
using Microsoft.Extensions.Logging;

namespace Cinomni.Subtitles.Providers;

/// <summary>
/// Production <see cref="ISubtitleProvider"/> over the OpenSubtitles REST API (MVP). Uses an
/// SSRF-hardened <see cref="HttpClient"/> (configured at registration), sends the referenced API key as
/// a header, and treats every response as untrusted (defensive JSON parsing; provider content is
/// never evaluated or executed). Not exercised by unit tests (no network/credentials in dev); tests use a
/// mock provider. Search matching by movie hash and per-provider backoff are later refinements.
/// </summary>
public sealed class OpenSubtitlesProvider(
    HttpClient httpClient,
    SubtitleProviderOptions options,
    ILogger<OpenSubtitlesProvider> logger) : ISubtitleProvider
{
    public string Name => "opensubtitles";

    /// <inheritdoc />
    public bool IsConfigured => !string.IsNullOrEmpty(options.ApiKey);

    public async Task<IReadOnlyList<ProviderCandidate>> SearchAsync(SubtitleProviderQuery query, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return [];
        }

        var uri = BuildSearchUri(query);
        try
        {
            using var request = ApiRequest(HttpMethod.Get, uri);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return Parse(await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken), query);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "OpenSubtitles search failed for '{Release}' ({Language}).", query.Release, query.Language);
            return [];
        }
    }

    public async Task<ProviderDownload> DownloadAsync(string downloadRef, CancellationToken cancellationToken = default)
    {
        // Resolve the temporary link for the file, then fetch it (both over the SSRF-hardened client).
        using var linkRequest = ApiRequest(HttpMethod.Post, "download");
        linkRequest.Content = JsonContent.Create(new { file_id = ParseFileId(downloadRef) });
        using var linkResponse = await httpClient.SendAsync(linkRequest, cancellationToken);
        linkResponse.EnsureSuccessStatusCode();
        using var linkDocument = await JsonDocument.ParseAsync(
            await linkResponse.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var link = linkDocument.RootElement.TryGetProperty("link", out var linkElement) ? linkElement.GetString() : null;
        if (string.IsNullOrEmpty(link))
        {
            throw new InvalidOperationException("OpenSubtitles download response had no link.");
        }

        // Without the key: the link is a host the provider's response chose, and the key is a credential
        // for the provider's API, not for whoever serves its files.
        var content = await httpClient.GetByteArrayAsync(link, cancellationToken);
        return new ProviderDownload(content, SubtitleFormat.Srt);
    }

    /// <summary>A request to the OpenSubtitles API itself, the only kind that carries the key.</summary>
    private HttpRequestMessage ApiRequest(HttpMethod method, string uri)
    {
        var request = new HttpRequestMessage(method, uri);
        // Required, not a courtesy: without it the API answers POST download with its HTML 503 page.
        request.Headers.Accept.ParseAdd("application/json");
        if (!string.IsNullOrEmpty(options.ApiKey))
        {
            request.Headers.Add("Api-Key", options.ApiKey);
        }

        return request;
    }

    /// <summary>
    /// Builds the <c>GET subtitles</c> query string. For an episode the search term is the <em>series</em>
    /// title and the episode is pinned with <c>season_number</c>/<c>episode_number</c> (plus
    /// <c>parent_imdb_id</c> when the catalog knows one): the API narrows on those, whereas a full release
    /// name as <c>query</c> is matched loosely and returns the same broad set for every episode of a show.
    /// A movie is searched by its IMDb id when the catalog knows one, else by its catalog title and year
    /// as one search text,
    /// and only as a last resort by the release name — which matched another film on a shared group tag.
    /// <para>
    /// The address has to be the API's one canonical form, or it answers 301 with that form instead —
    /// and this client follows no redirect, so any other spelling is a failed search. Canonical means the
    /// parameters in alphabetical order, every value lowercased before it is escaped, a space as
    /// <c>+</c>, and an IMDb id as the bare number without <c>tt</c> or leading zeros.
    /// </para>
    /// </summary>
    private static string BuildSearchUri(SubtitleProviderQuery query)
    {
        var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["languages"] = query.Language,
        };

        if (query.IsEpisode)
        {
            parameters["query"] = string.IsNullOrWhiteSpace(query.SeriesTitle) ? query.Release : query.SeriesTitle;
            parameters["season_number"] = query.SeasonNumber!.Value.ToString(CultureInfo.InvariantCulture);
            parameters["episode_number"] = query.EpisodeNumber!.Value.ToString(CultureInfo.InvariantCulture);
            if (SubtitleCandidateScorer.ImdbNumber(query.ParentImdbId) is { } series)
            {
                parameters["parent_imdb_id"] = series;
            }
        }
        else if (SubtitleCandidateScorer.ImdbNumber(query.ImdbId) is { } film)
        {
            parameters["imdb_id"] = film;
        }
        else if (!string.IsNullOrWhiteSpace(query.Title))
        {
            // The year goes in the text, not in the API's year filter: measured against the live API,
            // "28 days later" with year=2002 found nothing, the bare title found other works, and
            // "28 days later 2002" found the film's 46 subtitles. The scorer checks the year itself.
            parameters["query"] = query.Year is { } year
                ? string.Create(CultureInfo.InvariantCulture, $"{query.Title} {year}")
                : query.Title;
        }
        else
        {
            parameters["query"] = query.Release;
        }

        return "subtitles?" + string.Join('&', parameters.Select(p => $"{p.Key}={CanonicalValue(p.Value)}"));
    }

    private static string CanonicalValue(string value) =>
        Uri.EscapeDataString(value.ToLowerInvariant()).Replace("%20", "+", StringComparison.Ordinal);

    private IReadOnlyList<ProviderCandidate> Parse(JsonDocument document, SubtitleProviderQuery query)
    {
        var candidates = new List<ProviderCandidate>();
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return candidates;
        }

        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("attributes", out var attributes))
            {
                continue;
            }

            var release = GetString(attributes, "release") ?? query.Release;
            var hearingImpaired = attributes.TryGetProperty("hearing_impaired", out var hi) && hi.ValueKind == JsonValueKind.True;
            var forced = attributes.TryGetProperty("foreign_parts_only", out var foreign) && foreign.ValueKind == JsonValueKind.True;
            var fileId = FirstFileId(attributes);
            if (fileId is not null)
            {
                var scored = SubtitleCandidateScorer.Score(query, Evidence(attributes, release));
                candidates.Add(new ProviderCandidate(release, scored.Score, hearingImpaired, SubtitleFormat.Srt, fileId, forced, scored.Reason));
            }
        }

        return candidates;
    }

    /// <summary>
    /// What the API publishes about the file beside its name: the title it was filed under — a film, or
    /// an episode with its series — and how it was made. Read defensively: any field may be missing or
    /// of another type, and then it simply says nothing.
    /// </summary>
    private static CandidateEvidence Evidence(JsonElement attributes, string release)
    {
        var details = attributes.TryGetProperty("feature_details", out var feature) && feature.ValueKind == JsonValueKind.Object
            ? feature
            : default;
        var isEpisode = details.ValueKind == JsonValueKind.Object
            && string.Equals(GetString(details, "feature_type"), "Episode", StringComparison.OrdinalIgnoreCase);
        var machine = IsTrue(attributes, "machine_translated") || IsTrue(attributes, "ai_translated");
        var downloads = GetInt(attributes, "download_count") ?? 0;

        if (details.ValueKind != JsonValueKind.Object)
        {
            return new CandidateEvidence(release, DownloadCount: downloads, Trusted: IsTrue(attributes, "from_trusted"), MachineTranslated: machine);
        }

        return isEpisode
            ? new CandidateEvidence(
                release,
                Title: GetString(details, "parent_title"),
                ImdbId: GetInt(details, "imdb_id")?.ToString(CultureInfo.InvariantCulture),
                ParentImdbId: GetInt(details, "parent_imdb_id")?.ToString(CultureInfo.InvariantCulture),
                Season: GetInt(details, "season_number"),
                Episode: GetInt(details, "episode_number"),
                DownloadCount: downloads,
                Trusted: IsTrue(attributes, "from_trusted"),
                MachineTranslated: machine)
            : new CandidateEvidence(
                release,
                Title: GetString(details, "title"),
                Year: GetInt(details, "year") is { } year and > 1800 ? year : null,
                ImdbId: GetInt(details, "imdb_id")?.ToString(CultureInfo.InvariantCulture),
                DownloadCount: downloads,
                Trusted: IsTrue(attributes, "from_trusted"),
                MachineTranslated: machine);
    }

    private static bool IsTrue(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static int? GetInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;

    private static string? FirstFileId(JsonElement attributes)
    {
        if (attributes.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
        {
            foreach (var file in files.EnumerateArray())
            {
                if (file.TryGetProperty("file_id", out var id) && id.TryGetInt64(out var fileId))
                {
                    return fileId.ToString(CultureInfo.InvariantCulture);
                }
            }
        }

        return null;
    }

    private static long ParseFileId(string downloadRef) =>
        long.TryParse(downloadRef, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0;

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
