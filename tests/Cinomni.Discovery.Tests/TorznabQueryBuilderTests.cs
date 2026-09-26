using Cinomni.Discovery.Contracts;
using Cinomni.Discovery.Indexers;
using Cinomni.Search.Contracts;

namespace Cinomni.Discovery.Tests;

/// <summary>
/// Unit tests for the Torznab query composer — the first coverage this code has ever had. Each fact
/// pins the <b>exact</b> query string for one mode: the composed URL is the only thing an indexer
/// sees, and a wrong one comes back as an empty result set that is indistinguishable from a genuine
/// "nothing available".
/// </summary>
public sealed class TorznabQueryBuilderTests
{
    private const string BaseUrl = "https://idx.example/api";

    [Fact]
    public void Movie_builds_t_movie_with_imdbid_and_categories()
    {
        // Arrange
        var indexer = Indexer(new IndexerCapabilities(MovieCategories: [2000, 2040]));
        var criterion = new SearchCriterion("Interstellar", 2014, "tt0816692", "157336", "Movie");

        // Act
        var url = TorznabQueryBuilder.BuildRequestUrl(indexer, criterion);

        // Assert — the imdb id loses its "tt" prefix; the configured categories ride along.
        Assert.Equal(
            $"{BaseUrl}?t=movie&q=Interstellar&year=2014&imdbid=0816692&tmdbid=157336&cat=2000,2040",
            url);
    }

    [Fact]
    public void A_movie_on_an_unconfigured_indexer_is_queried_exactly_as_before_capabilities_existed()
    {
        // Arrange — no capabilities at all, which is every indexer registered before this change.
        var indexer = Indexer(capabilities: null);
        var criterion = new SearchCriterion("Interstellar", 2014, "tt0816692", "157336", "Movie");

        // Act
        var url = TorznabQueryBuilder.BuildRequestUrl(indexer, criterion);

        // Assert — no cat= appears, so existing installs see identical movie results.
        Assert.Equal($"{BaseUrl}?t=movie&q=Interstellar&year=2014&imdbid=0816692&tmdbid=157336", url);
    }

    [Fact]
    public void Episode_builds_t_tvsearch_with_season_and_ep()
    {
        // Arrange
        var indexer = Indexer(new IndexerCapabilities(TvCategories: [5000]));
        var criterion = new SearchCriterion(
            "The Wire", 2002, null, null, "Episode", SeasonNumber: 2, EpisodeNumber: 5, TvdbId: "79126");

        // Act
        var url = TorznabQueryBuilder.BuildRequestUrl(indexer, criterion);

        // Assert — the term stays the series title; the numbers travel as parameters.
        Assert.Equal($"{BaseUrl}?t=tvsearch&q=The%20Wire&tvdbid=79126&season=2&ep=5&cat=5000", url);
    }

    [Fact]
    public void Season_pack_builds_t_tvsearch_with_season_only()
    {
        // Arrange
        var indexer = Indexer(new IndexerCapabilities(TvCategories: [5000]));
        var criterion = new SearchCriterion("The Wire", null, null, null, "Season", SeasonNumber: 2, TvdbId: "79126");

        // Act
        var url = TorznabQueryBuilder.BuildRequestUrl(indexer, criterion);

        // Assert
        Assert.Equal($"{BaseUrl}?t=tvsearch&q=The%20Wire&tvdbid=79126&season=2&cat=5000", url);
    }

    [Fact]
    public void Absolute_numbering_falls_back_to_free_text()
    {
        // Arrange — Torznab has no parameter for an anime absolute number.
        var indexer = Indexer(new IndexerCapabilities(TvCategories: [5070]));
        var criterion = new SearchCriterion(
            "One Piece", null, null, null, "Episode", AbsoluteNumber: 1071, TvdbId: "81797");

        // Act
        var url = TorznabQueryBuilder.BuildRequestUrl(indexer, criterion);

        // Assert — still tvsearch, but the number is folded into q and no season/ep is sent.
        Assert.Equal($"{BaseUrl}?t=tvsearch&q=One%20Piece%201071&tvdbid=81797&cat=5070", url);
    }

    [Fact]
    public void Date_based_falls_back_to_free_text()
    {
        // Arrange
        var indexer = Indexer(IndexerCapabilities.Unknown);
        var criterion = new SearchCriterion(
            "The Daily Show", null, null, null, "Episode", AirDate: new DateOnly(2026, 7, 28));

        // Act
        var url = TorznabQueryBuilder.BuildRequestUrl(indexer, criterion);

        // Assert
        Assert.Equal($"{BaseUrl}?t=tvsearch&q=The%20Daily%20Show%202026%2007%2028", url);
    }

    [Fact]
    public void An_indexer_without_tvsearch_falls_back_to_t_search()
    {
        // Arrange
        var indexer = Indexer(new IndexerCapabilities(SupportsTvSearch: false, TvCategories: [5000]));
        var criterion = new SearchCriterion(
            "The Wire", null, null, null, "Episode", SeasonNumber: 2, EpisodeNumber: 5, TvdbId: "79126");

        // Act
        var url = TorznabQueryBuilder.BuildRequestUrl(indexer, criterion);

        // Assert — no tvsearch-only parameter survives; the numbering moves into the free text, so
        // the query is still meaningful instead of returning the whole series.
        Assert.Equal($"{BaseUrl}?t=search&q=The%20Wire%20S02E05&cat=5000", url);
    }

    [Fact]
    public void An_indexer_without_a_movie_mode_falls_back_to_t_search()
    {
        // Arrange
        var indexer = Indexer(new IndexerCapabilities(SupportsMovieSearch: false));
        var criterion = new SearchCriterion("Interstellar", 2014, "tt0816692", "157336", "Movie");

        // Act
        var url = TorznabQueryBuilder.BuildRequestUrl(indexer, criterion);

        // Assert — the id parameters belong to t=movie and are dropped with it.
        Assert.Equal($"{BaseUrl}?t=search&q=Interstellar&year=2014", url);
    }

    [Fact]
    public void A_tvdb_id_is_not_imdb_normalised()
    {
        // Arrange — a naive TrimStart('t','T') would eat the leading digits of any id starting with
        // those characters; more importantly, a tvdb id has no "tt" convention at all.
        var indexer = Indexer(IndexerCapabilities.Unknown);
        var criterion = new SearchCriterion(
            "Show", null, "tt1234567", null, "Episode", SeasonNumber: 1, EpisodeNumber: 1, TvdbId: "tt42");

        // Act
        var url = TorznabQueryBuilder.BuildRequestUrl(indexer, criterion);

        // Assert — tvdbid is verbatim, imdbid loses exactly the "tt".
        Assert.Contains("tvdbid=tt42", url, StringComparison.Ordinal);
        Assert.Contains("imdbid=1234567", url, StringComparison.Ordinal);
    }

    [Fact]
    public void An_undeclared_parameter_is_omitted()
    {
        // Arrange — an endpoint that declares tvsearch but only q and season (no ep, no tvdbid).
        var indexer = Indexer(new IndexerCapabilities(TvSearchParams: ["q", "season"]));
        var criterion = new SearchCriterion(
            "The Wire", null, null, null, "Episode", SeasonNumber: 2, EpisodeNumber: 5, TvdbId: "79126");

        // Act
        var url = TorznabQueryBuilder.BuildRequestUrl(indexer, criterion);

        // Assert
        Assert.Equal($"{BaseUrl}?t=tvsearch&q=The%20Wire&season=2", url);
    }

    [Fact]
    public void A_credential_appends_apikey_and_changes_nothing_else()
    {
        // The pinned order matters as much as the key: an indexer that gains a credential must send
        // the query it already sent, plus one parameter — not a reshuffled one.
        var indexer = Indexer(new IndexerCapabilities(MovieCategories: [2000]));
        var criterion = new SearchCriterion("Interstellar", 2014, "tt0816692", "157336", "Movie");

        var withoutKey = TorznabQueryBuilder.BuildRequestUrl(indexer, criterion);
        var withKey = TorznabQueryBuilder.BuildRequestUrl(
            indexer, criterion, new IndexerCredential(null, "s3cr3t"));

        Assert.Equal($"{withoutKey}&apikey=s3cr3t", withKey);
    }

    [Fact]
    public void No_credential_sends_no_apikey_at_all()
    {
        // Not an empty apikey= : an endpoint that reads the parameter's presence must see nothing.
        var url = TorznabQueryBuilder.BuildRequestUrl(
            Indexer(null), new SearchCriterion("Interstellar", 2014, null, null, "Movie"), credential: null);

        Assert.DoesNotContain("apikey", url, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_username_and_password_never_reach_the_query_because_they_travel_as_basic_auth()
    {
        var url = TorznabQueryBuilder.BuildRequestUrl(
            Indexer(null),
            new SearchCriterion("Interstellar", 2014, null, null, "Movie"),
            new IndexerCredential("operator", "s3cr3t"));

        Assert.DoesNotContain("operator", url, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("s3cr3t", url, StringComparison.Ordinal);
        Assert.DoesNotContain("apikey", url, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_key_with_url_unsafe_characters_is_encoded_rather_than_breaking_the_query()
    {
        var url = TorznabQueryBuilder.BuildRequestUrl(
            Indexer(null),
            new SearchCriterion("Interstellar", 2014, null, null, "Movie"),
            new IndexerCredential(null, "a b&c=d"));

        // Unescaped, the & would end the parameter and turn the rest of the key into query keys.
        Assert.EndsWith("&apikey=a%20b%26c%3Dd", url, StringComparison.Ordinal);
    }

    private static IndexerSummary Indexer(IndexerCapabilities? capabilities) =>
        new(IndexerId.New(), "Idx", IndexerProtocol.Torznab, BaseUrl, 1, true, capabilities);
}
