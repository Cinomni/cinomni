using Cinomni.Metadata.Contracts;
using Cinomni.Metadata.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Metadata.Tests;

/// <summary>
/// Cross-provider de-duplication of search candidates, driven through the real
/// search service with three fakes. Without it a series search returns the same show once per provider
/// and the operator has no way to tell which row to add.
/// </summary>
public sealed class MetadataCandidateMergeTests
{
    [Fact]
    public async Task The_same_show_found_through_three_providers_collapses_to_one_row()
    {
        // tmdb knows only its own id; tvdb cross-references imdb and tmdb; tvmaze cross-references tvdb.
        // No two of them share a title spelling, so only the external ids can tie the rows together.
        var tmdb = Source("tmdb", Candidate("1438", "The Frontier", 2002, tmdbId: "1438"));
        var tvdb = Source("tvdb", Candidate("79126", "Frontier, The", 2002, tvdbId: "79126", imdbId: "tt0306414", tmdbId: "1438"));
        var tvmaze = Source("tvmaze", Candidate("82", "The  Frontier!", 2002, tvdbId: "79126"));

        var results = await SearchAsync([tvmaze, tvdb, tmdb]);

        var candidate = Assert.Single(results);
        // The highest-priority provider's row survives; the ids the duplicates knew are unioned in.
        Assert.Equal("tmdb", candidate.Provider);
        Assert.Equal("1438", candidate.ExternalId);
        Assert.Equal("The Frontier", candidate.Title);
        Assert.Equal("79126", candidate.TvdbId);
        Assert.Equal("tt0306414", candidate.ImdbId);
        Assert.Equal("1438", candidate.TmdbId);
    }

    [Fact]
    public async Task An_id_learned_from_one_duplicate_merges_a_later_one()
    {
        // tvmaze shares nothing with tmdb directly — only with the tvdb row that merged in between.
        var tmdb = Source("tmdb", Candidate("1438", "The Frontier", 2002, tmdbId: "1438"));
        var tvdb = Source("tvdb", Candidate("79126", "Frontier, The", 2002, tvdbId: "79126", tmdbId: "1438"));
        var tvmaze = Source("tvmaze", Candidate("82", "Frontier", 1999, tvdbId: "79126"));

        var results = await SearchAsync([tmdb, tvdb, tvmaze]);

        Assert.Single(results);
    }

    [Fact]
    public async Task Different_shows_are_never_merged()
    {
        var tmdb = Source("tmdb", Candidate("1", "The Frontier", 2002, tmdbId: "1"));
        var tvdb = Source("tvdb", Candidate("2", "Another Frontier", 2002, tvdbId: "2"));

        var results = await SearchAsync([tmdb, tvdb]);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task A_shared_title_and_year_merges_when_no_external_id_matches()
    {
        // Punctuation and casing differ; the year is the same. This is the fallback key.
        var tmdb = Source("tmdb", Candidate("1", "Marvel's Daredevil", 2015));
        var tvdb = Source("tvdb", Candidate("2", "Marvels Daredevil", 2015));

        var results = await SearchAsync([tmdb, tvdb]);

        Assert.Single(results);
    }

    [Fact]
    public async Task A_shared_title_without_a_year_is_not_enough_to_merge()
    {
        // Two shows can share a name; without a year there is nothing to corroborate it, and a wrong
        // merge hides a row the operator may have been looking for.
        var tmdb = Source("tmdb", Candidate("1", "The Frontier", null));
        var tvdb = Source("tvdb", Candidate("2", "The Frontier", null));

        var results = await SearchAsync([tmdb, tvdb]);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task A_shared_title_with_different_years_is_not_merged()
    {
        var tmdb = Source("tmdb", Candidate("1", "The Frontier", 2002));
        var tvdb = Source("tvdb", Candidate("2", "The Frontier", 2019));

        var results = await SearchAsync([tmdb, tvdb]);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task A_movie_search_still_merges_on_a_shared_external_id()
    {
        // An external id identifies a work, so this merge cannot be wrong for any media kind.
        var tmdb = Source("tmdb", MetadataMediaKind.Movie, Candidate("603", "The Matrix", 1999, tmdbId: "603"));
        var tvdb = Source("tvdb", MetadataMediaKind.Movie, Candidate("169", "Matrix, The", 1999, tmdbId: "603"));

        var results = await SearchAsync([tmdb, tvdb], MetadataMediaKind.Movie);

        Assert.Single(results);
    }

    [Fact]
    public async Task A_movie_search_does_not_merge_on_a_shared_title_and_year()
    {
        // The title+year fallback is a heuristic and stays off for movies: the movie search surface
        // keeps the behaviour it ships with, and two distinct films sharing both are never collapsed.
        var tmdb = Source("tmdb", MetadataMediaKind.Movie, Candidate("1", "Crash", 2004));
        var tvdb = Source("tvdb", MetadataMediaKind.Movie, Candidate("2", "Crash", 2004));

        var results = await SearchAsync([tmdb, tvdb], MetadataMediaKind.Movie);

        Assert.Equal(2, results.Count);
    }

    // -- helpers ---------------------------------------------------------------------------------

    private static ProviderMetadataCandidate Candidate(
        string externalId,
        string title,
        int? year,
        string? tvdbId = null,
        string? imdbId = null,
        string? tmdbId = null) =>
        new(externalId, title, year, null, new ProviderExternalIds(tvdbId, imdbId, tmdbId));

    private static FakeMetadataSource Source(string name, params ProviderMetadataCandidate[] candidates) =>
        Source(name, MetadataMediaKind.Series, candidates);

    private static FakeMetadataSource Source(
        string name,
        MetadataMediaKind kind,
        params ProviderMetadataCandidate[] candidates) =>
        new(name, kind) { Candidates = [.. candidates] };

    private static async Task<IReadOnlyList<MetadataCandidate>> SearchAsync(
        IReadOnlyList<IMetadataSource> sources,
        MetadataMediaKind kind = MetadataMediaKind.Series)
    {
        // The service is a pure fan-out over its sources; no database is involved in a search.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new Application.MetadataOptions());
        foreach (var source in sources)
        {
            services.AddSingleton(source);
        }

        services.AddScoped<IMetadataSearch, Application.MetadataSearchService>();

        await using var provider = services.BuildServiceProvider();
        var result = await provider.GetRequiredService<IMetadataSearch>()
            .SearchAsync("frontier", year: null, kind);
        Assert.True(result.IsSuccess, result.Error.Message);
        return result.Value;
    }
}
