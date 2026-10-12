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
    /// A movie keeps the previous behaviour exactly — the release name as the query and nothing else.
    /// <para>
    /// The address has to be the API's one canonical form, or it answers 301 with that form instead —
    /// and this client follows no redirect, so any other spelling is a failed search. Canonical means the
    /// parameters in alphabetical order, every value lowercased before it is escaped, a space as
    /// <c>+</c>, and an IMDb id as the bare number without <c>tt</c> or leading zeros.
    /// </para>
    /// </summary>
    private static string BuildSearchUri(SubtitleProviderQuery query)
    {
        var term = query.IsEpisode && !string.IsNullOrWhiteSpace(query.SeriesTitle) ? query.SeriesTitle! : query.Release;
        var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["languages"] = query.Language,
            ["query"] = term,
        };

        if (query.SeasonNumber is { } season)
        {
            parameters["season_number"] = season.ToString(CultureInfo.InvariantCulture);
        }

        if (query.EpisodeNumber is { } episode)
        {
            parameters["episode_number"] = episode.ToString(CultureInfo.InvariantCulture);
        }

        if (ImdbNumber(query.ParentImdbId) is { } imdb)
        {
            parameters["parent_imdb_id"] = imdb;
        }

        return "subtitles?" + string.Join('&', parameters.Select(p => $"{p.Key}={CanonicalValue(p.Value)}"));
    }

    private static string CanonicalValue(string value) =>
        Uri.EscapeDataString(value.ToLowerInvariant()).Replace("%20", "+", StringComparison.Ordinal);

    /// <summary>
    /// The number in a title id such as <c>tt0898266</c>, or <c>null</c> for anything that is not one —
    /// a person id, an empty value, or text that would otherwise reach the query.
    /// </summary>
    private static string? ImdbNumber(string? imdbId)
    {
        if (string.IsNullOrWhiteSpace(imdbId))
        {
            return null;
        }

        var digits = imdbId.StartsWith("tt", StringComparison.OrdinalIgnoreCase) ? imdbId[2..] : imdbId;
        if (digits.Length == 0 || !digits.All(char.IsAsciiDigit))
        {
            return null;
        }

        var number = digits.TrimStart('0');
        return number.Length == 0 ? null : number;
    }

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
            var score = ScoreOf(attributes);
            var fileId = FirstFileId(attributes);
            if (fileId is not null)
            {
                candidates.Add(new ProviderCandidate(release, score, hearingImpaired, SubtitleFormat.Srt, fileId, forced));
            }
        }

        return candidates;
    }

    private static int ScoreOf(JsonElement attributes)
    {
        var rating = attributes.TryGetProperty("ratings", out var r) && r.TryGetDouble(out var value) ? value : 0;
        return (int)Math.Round(Math.Clamp(rating, 0, 10) * 10);
    }

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
