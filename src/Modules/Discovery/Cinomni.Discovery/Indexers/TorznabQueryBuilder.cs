using System.Globalization;
using Cinomni.Discovery.Contracts;
using Cinomni.Search.Contracts;
using Microsoft.AspNetCore.WebUtilities;

namespace Cinomni.Discovery.Indexers;

/// <summary>
/// Composes the Torznab/Newznab query for one criterion against one indexer. Pure and side-effect
/// free — extracted out of the HTTP client for the same reason <see cref="TorznabFeedParser"/> was:
/// the fiddly part must be unit-testable, and the series slice turns a two-branch decision into
/// half a dozen.
/// <para>
/// The mode is chosen from the content kind and the indexer's declared capabilities: a movie asks
/// <c>t=movie</c>, a series/season/episode asks <c>t=tvsearch</c>, and an endpoint that declares it
/// cannot do a mode falls back to the universally supported <c>t=search</c> with the numbering folded
/// into the free-text query. That fallback is load-bearing rather than cosmetic:
/// <c>ReleaseSearch.SearchOneAsync</c> swallows every exception and returns no candidates, so an
/// indexer that rejects <c>t=tvsearch</c> is indistinguishable from a genuine "nothing available".
/// </para>
/// </summary>
internal static class TorznabQueryBuilder
{
    /// <summary>Torznab search modes (the <c>t=</c> parameter).</summary>
    public const string MovieMode = "movie";
    public const string TvSearchMode = "tvsearch";
    public const string BasicMode = "search";

    private static readonly string[] SeriesContentKinds = ["Series", "Season", "Episode"];

    /// <summary>
    /// Whether a Torznab/Newznab credential is a username and password (HTTP Basic, typically an
    /// endpoint behind an authenticating proxy) rather than an API key. The stored username is the
    /// discriminator: an API key has none.
    /// </summary>
    public static bool IsBasicAuth(IndexerCredential credential) => !string.IsNullOrEmpty(credential.Username);

    /// <summary>The absolute request URL for this criterion, ready to hand to <c>HttpClient</c>.</summary>
    public static string BuildRequestUrl(
        IndexerSummary indexer, SearchCriterion criterion, IndexerCredential? credential = null) =>
        QueryHelpers.AddQueryString(indexer.BaseUrl, BuildQuery(indexer, criterion, credential));

    /// <summary>
    /// The ordered query parameters. Order is part of the contract so tests can pin it, which is why
    /// <c>apikey</c> is appended last rather than inserted among the search parameters: an indexer
    /// that gains a credential must produce the same query it did before, plus one.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string?>> BuildQuery(
        IndexerSummary indexer,
        SearchCriterion criterion,
        IndexerCredential? credential = null)
    {
        var capabilities = indexer.Capabilities ?? IndexerCapabilities.Unknown;
        var parameters = IsSeriesKind(criterion.ContentKind)
            ? BuildTvQuery(capabilities, criterion)
            : BuildMovieQuery(capabilities, criterion);

        // A credential with a username is an account password, sent as HTTP Basic by the client
        // (TorznabIndexerClient) — never in the query, where it would reach every access log.
        if (credential is null || IsBasicAuth(credential))
        {
            return parameters;
        }

        // Torznab and Newznab both take the key as `apikey`.
        return [.. parameters, new KeyValuePair<string, string?>("apikey", credential.Secret)];
    }

    private static bool IsSeriesKind(string contentKind) =>
        SeriesContentKinds.Any(k => string.Equals(k, contentKind, StringComparison.OrdinalIgnoreCase));

    // -- movie ------------------------------------------------------------------------------------

    private static List<KeyValuePair<string, string?>> BuildMovieQuery(
        IndexerCapabilities capabilities,
        SearchCriterion criterion)
    {
        var structured = capabilities.SupportsMovieSearch;
        var query = new List<KeyValuePair<string, string?>>
        {
            Param("t", structured ? MovieMode : BasicMode),
            Param("q", criterion.Term),
        };

        if (criterion.Year is int year && capabilities.SupportsMovieParam(IndexerCapabilities.ParamNames.Year))
        {
            query.Add(Param("year", year.ToString(CultureInfo.InvariantCulture)));
        }

        if (structured
            && !string.IsNullOrWhiteSpace(criterion.ImdbId)
            && capabilities.SupportsMovieParam(IndexerCapabilities.ParamNames.ImdbId))
        {
            query.Add(Param("imdbid", NormalizeImdbId(criterion.ImdbId)));
        }

        if (structured
            && !string.IsNullOrWhiteSpace(criterion.TmdbId)
            && capabilities.SupportsMovieParam(IndexerCapabilities.ParamNames.TmdbId))
        {
            query.Add(Param("tmdbid", criterion.TmdbId));
        }

        AddCategories(query, capabilities.MovieCategories);
        return query;
    }

    // -- series -----------------------------------------------------------------------------------

    private static List<KeyValuePair<string, string?>> BuildTvQuery(
        IndexerCapabilities capabilities,
        SearchCriterion criterion)
    {
        var structured = capabilities.SupportsTvSearch;

        // Torznab has no parameter for an absolute (anime) number or an air date, so those two
        // families always ride in the free-text query even in tvsearch mode.
        var useSeasonParams = structured && criterion.SeasonNumber is int;

        var query = new List<KeyValuePair<string, string?>>
        {
            Param("t", structured ? TvSearchMode : BasicMode),
            Param("q", useSeasonParams ? criterion.Term : FreeText(criterion)),
        };

        if (structured
            && !string.IsNullOrWhiteSpace(criterion.TvdbId)
            && capabilities.SupportsTvParam(IndexerCapabilities.ParamNames.TvdbId))
        {
            // NOT imdb-normalised: stripping leading characters off a numeric TheTVDB id would
            // silently corrupt it (a "tt"-prefix trim is an IMDb-only convention).
            query.Add(Param("tvdbid", criterion.TvdbId));
        }

        if (structured
            && !string.IsNullOrWhiteSpace(criterion.ImdbId)
            && capabilities.SupportsTvParam(IndexerCapabilities.ParamNames.ImdbId))
        {
            query.Add(Param("imdbid", NormalizeImdbId(criterion.ImdbId)));
        }

        if (useSeasonParams && capabilities.SupportsTvParam(IndexerCapabilities.ParamNames.Season))
        {
            query.Add(Param("season", criterion.SeasonNumber!.Value.ToString(CultureInfo.InvariantCulture)));

            if (criterion.EpisodeNumber is int episode
                && capabilities.SupportsTvParam(IndexerCapabilities.ParamNames.Episode))
            {
                query.Add(Param("ep", episode.ToString(CultureInfo.InvariantCulture)));
            }
        }

        AddCategories(query, capabilities.TvCategories);
        return query;
    }

    /// <summary>
    /// The numbering folded into the term, for the modes that cannot express it as a parameter
    /// (absolute anime numbering, date-based dailies, and any <c>t=search</c> fallback).
    /// </summary>
    private static string FreeText(SearchCriterion criterion)
    {
        if (criterion.SeasonNumber is int season)
        {
            return criterion.EpisodeNumber is int episode
                ? $"{criterion.Term} S{season:00}E{episode:00}"
                : $"{criterion.Term} S{season:00}";
        }

        if (criterion.AbsoluteNumber is int absolute)
        {
            return $"{criterion.Term} {absolute.ToString(CultureInfo.InvariantCulture)}";
        }

        if (criterion.AirDate is DateOnly airDate)
        {
            return $"{criterion.Term} {airDate.ToString("yyyy MM dd", CultureInfo.InvariantCulture)}";
        }

        return criterion.Term;
    }

    private static void AddCategories(List<KeyValuePair<string, string?>> query, IReadOnlyList<int>? categories)
    {
        if (categories is not { Count: > 0 })
        {
            // No category configured means no cat= at all — the pre-capabilities behaviour, and the
            // only safe default: hardcoded 2000/5000 are wrong for an indexer with a custom map.
            return;
        }

        query.Add(Param("cat", string.Join(',', categories.Select(c => c.ToString(CultureInfo.InvariantCulture)))));
    }

    /// <summary>Torznab expects the numeric IMDb id, without the <c>tt</c> prefix.</summary>
    private static string NormalizeImdbId(string imdbId) =>
        imdbId.StartsWith("tt", StringComparison.OrdinalIgnoreCase) ? imdbId[2..] : imdbId;

    private static KeyValuePair<string, string?> Param(string name, string? value) => new(name, value);
}
