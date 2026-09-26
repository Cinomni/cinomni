using System.Globalization;
using System.Text.Json;
using Cinomni.Subtitles.Contracts;
using Microsoft.Extensions.Logging;

namespace Cinomni.Subtitles.Providers;

/// <summary>SubDL REST search. Unconfigured when no API key is set, so it costs no request.</summary>
public sealed class SubdlProviderOptions
{
    public string BaseAddress { get; set; } = "https://api.subdl.com/api/v1/";

    public string ApiKey { get; set; } = string.Empty;

    public string UserAgent { get; set; } = "Cinomni/1.0";

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Production <see cref="ISubtitleProvider"/> over SubDL's published search API. The key travels as a
/// query parameter because that is how the search endpoint requires it, and it is never written to a
/// log. Downloads are confined to <c>dl.subdl.com</c> and refused unless the bytes look like a subtitle.
/// </summary>
public sealed class SubdlSubtitleProvider(
    HttpClient httpClient,
    SubdlProviderOptions options,
    ILogger<SubdlSubtitleProvider> logger) : ISubtitleProvider
{
    public string Name => "subdl";

    public bool IsConfigured => !string.IsNullOrEmpty(options.ApiKey);

    public async Task<IReadOnlyList<ProviderCandidate>> SearchAsync(
        SubtitleProviderQuery query,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return [];
        }

        try
        {
            using var response = await httpClient.GetAsync(BuildSearchUri(query), cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return SubdlSubtitleParser.Parse(document.RootElement, query);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(
                "Subdl search failed for '{Release}' ({Language}): {Reason}.",
                query.Release,
                query.Language,
                ex.GetType().Name);
            return [];
        }
    }

    public async Task<ProviderDownload> DownloadAsync(string downloadRef, CancellationToken cancellationToken = default)
    {
        var uri = SubdlSubtitleParser.DownloadUri(downloadRef)
            ?? throw new InvalidOperationException("Subdl download reference was refused.");

        var content = await httpClient.GetByteArrayAsync(uri, cancellationToken);
        // The bytes decide the format, not the URL: a name read by substring stored The.Assassin.srt as
        // .ass, and a VobSub .sub as .srt.
        if (!SubdlSubtitleParser.IsSubtitleBytes(content)
            || SubdlSubtitleParser.FormatOfContent(content) is not { } format)
        {
            throw new InvalidOperationException("Subdl download was not a subtitle file.");
        }

        return new ProviderDownload(content, format);
    }

    private string BuildSearchUri(SubtitleProviderQuery query)
    {
        var language = query.Language.Length == 2
            ? query.Language.ToUpperInvariant()
            : query.Language;
        var term = query.IsEpisode && !string.IsNullOrWhiteSpace(query.SeriesTitle) ? query.SeriesTitle! : query.Release;
        var type = query.IsEpisode ? "tv" : "movie";
        var uri = "subtitles?api_key=" + Uri.EscapeDataString(options.ApiKey)
            + "&film_name=" + Uri.EscapeDataString(term)
            + "&type=" + type
            + "&languages=" + Uri.EscapeDataString(language)
            + "&hi=1&unpack=1&subs_per_page=10";

        if (query.SeasonNumber is { } season)
        {
            uri += "&season_number=" + Uri.EscapeDataString(season.ToString(CultureInfo.InvariantCulture));
        }

        if (query.EpisodeNumber is { } episode)
        {
            uri += "&episode_number=" + Uri.EscapeDataString(episode.ToString(CultureInfo.InvariantCulture));
        }

        return uri;
    }
}
