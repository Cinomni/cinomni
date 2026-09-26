using Cinomni.Metadata.Application;
using Cinomni.Metadata.Contracts;
using Cinomni.Metadata.Providers;
using Cinomni.Metadata.Tests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cinomni.Metadata.Tests;


/// <summary>
/// Parser tests for the TMDB adapter against scripted HTTP responses — no database, no network. TMDB is
/// the primary provider, so a series refresh routed to it used to be a silent no-op: it declared movies
/// only. It is also the one that does not embed episodes, so the per-season fan-out and its cap are the
/// interesting behaviour here. The movie path is asserted alongside as the regression.
/// </summary>
public sealed class TmdbSeriesParsingTests
{
    private const string BaseAddress = "https://tmdb.example/";
    private const string ImageBase = "https://images.example/original";

    private readonly FakeHttpMessageHandler _handler = new FakeHttpMessageHandler()
        .Respond("tv/3/season/0", TmdbFixtures.Season0)
        .Respond("tv/3/season/1", TmdbFixtures.Season1)
        .Respond("tv/3/season/2", TmdbFixtures.Season2)
        .Respond("tv/3?", TmdbFixtures.TvDetail)
        .Respond("search/tv", TmdbFixtures.SearchTv)
        .Respond("movie/603?", TmdbFixtures.MovieDetail);

    private TmdbMetadataSource CreateSource(Action<TmdbProviderOptions>? configure = null)
    {
        var options = new TmdbProviderOptions
        {
            ApiKey = "fake-key",
            BaseAddress = BaseAddress,
            ImageBaseAddress = ImageBase,
        };
        configure?.Invoke(options);
        return new TmdbMetadataSource(
            _handler.CreateClient(BaseAddress),
            options,
            new DisabledProviderNotice(NullLogger<DisabledProviderNotice>.Instance),
            NullLogger<TmdbMetadataSource>.Instance);
    }

    [Fact]
    public async Task Parses_tv_detail_and_seasons()
    {
        var result = await CreateSource().FetchAsync(TmdbFixtures.SeriesId, MetadataMediaKind.Series);

        Assert.Equal("Fake Frontier", result!.Title);
        Assert.Equal(2024, result.Year);
        // A series has no scalar "runtime" — it publishes episode_run_time as an array.
        Assert.Equal(44, result.RuntimeMinutes);

        var series = result.Series;
        Assert.NotNull(series);
        Assert.Equal(SeriesStatus.Continuing, series.Status); // TMDB spells it "Returning Series"
        Assert.Equal(new DateOnly(2024, 1, 8), series.FirstAired);
        Assert.Equal(new DateOnly(2025, 2, 3), series.LastAired);
        Assert.Equal(SeasonOrders.Official, series.SeasonOrder);

        // The detail lists them out of order; the specials bucket sorts first.
        Assert.Equal([0, 1, 2], series.Seasons.Select(s => s.Number));
        Assert.Equal($"{ImageBase}/s01.jpg", series.Seasons.Single(s => s.Number == 1).PosterUrl);
        Assert.Equal(2, series.Seasons.Single(s => s.Number == 1).EpisodeCount);
        Assert.Null(series.Seasons.Single(s => s.Number == 2).PosterUrl);
    }

    [Fact]
    public async Task Fetches_episodes_per_season_up_to_the_cap()
    {
        var result = await CreateSource(options => options.MaxSeasonRequests = 2)
            .FetchAsync(TmdbFixtures.SeriesId, MetadataMediaKind.Series);

        // Three seasons are listed but only two requests are allowed, so the third season contributes
        // nothing: the cap is what stops a pathological record fanning out against a rate-limited API.
        Assert.Equal(2, _handler.CountOf("/season/"));
        Assert.Equal(0, _handler.CountOf("/season/2"));

        var episodes = result!.Series!.Episodes;
        Assert.Equal([(0, 1), (1, 1), (1, 2)], episodes.Select(e => (e.SeasonNumber, e.Number)).ToList());
        Assert.True(episodes.Single(e => e.SeasonNumber == 0).IsSpecial);
        Assert.Equal($"{ImageBase}/s01e01.jpg", episodes.Single(e => e is { SeasonNumber: 1, Number: 1 }).StillUrl);
        // TMDB's air_date is date-only, so no timezone-aware instant is invented.
        Assert.Equal(new DateOnly(2024, 1, 8), episodes.Single(e => e is { SeasonNumber: 1, Number: 1 }).AirDate);
        Assert.All(episodes, e => Assert.Null(e.AirDateTime));
        Assert.All(episodes, e => Assert.Null(e.AbsoluteNumber));
    }

    [Fact]
    public async Task Every_season_is_fetched_when_the_cap_allows_it()
    {
        var result = await CreateSource().FetchAsync(TmdbFixtures.SeriesId, MetadataMediaKind.Series);

        Assert.Equal(3, _handler.CountOf("/season/"));
        Assert.Equal(4, result!.Series!.Episodes.Count);
    }

    [Fact]
    public async Task Reads_external_ids()
    {
        var result = await CreateSource().FetchAsync(TmdbFixtures.SeriesId, MetadataMediaKind.Series);

        var externalIds = result!.Series!.ExternalIds;
        Assert.Equal("900001", externalIds.TvdbId); // published as a number, normalised to text
        Assert.Equal("tt9000001", externalIds.ImdbId);
        Assert.Equal(TmdbFixtures.SeriesId, externalIds.TmdbId);
        Assert.Contains("external_ids", _handler.Requests[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_series_search_uses_the_tv_endpoint_and_carries_its_own_id()
    {
        var candidates = await CreateSource().SearchAsync(new MetadataProviderQuery("frontier", 2024, MetadataMediaKind.Series));

        var candidate = Assert.Single(candidates);
        Assert.Equal("3", candidate.ExternalId);
        Assert.Equal("Fake Frontier", candidate.Title);
        Assert.Equal(2024, candidate.Year);
        // A TMDB hit publishes no cross-references, but its own id is one — that is what lets a TheTVDB
        // hit carrying remoteIds[TheMovieDB] be recognised as the same show.
        Assert.Equal("3", candidate.ExternalIds!.TmdbId);
        Assert.Contains("search/tv?", _handler.Requests[0], StringComparison.Ordinal);
        Assert.Contains("first_air_date_year=2024", _handler.Requests[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_movie_fetch_is_unchanged()
    {
        var result = await CreateSource().FetchAsync(TmdbFixtures.MovieId, MetadataMediaKind.Movie);

        Assert.Equal("Fake Feature", result!.Title);
        Assert.Equal("Fake Feature", result.OriginalTitle);
        Assert.Equal(1999, result.Year);
        Assert.Equal(136, result.RuntimeMinutes);
        Assert.Equal($"{ImageBase}/movie-poster.jpg", result.PosterUrl);
        Assert.Null(result.Series); // no series block, and no season requests at all
        Assert.Equal(0, _handler.CountOf("/season/"));
        Assert.Single(result.Artwork, a => a.Kind == ArtworkKind.Poster);
    }

    [Fact]
    public async Task Without_an_api_key_nothing_is_fetched()
    {
        var result = await CreateSource(options => options.ApiKey = string.Empty)
            .FetchAsync(TmdbFixtures.SeriesId, MetadataMediaKind.Series);

        Assert.Null(result);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public void Tmdb_now_declares_series_support()
    {
        // Providers[0] is tmdb, so a series refresh routed to the primary provider used to find no
        // source that covered the kind and quietly do nothing at all.
        var kinds = CreateSource().SupportedKinds;

        Assert.Contains(MetadataMediaKind.Series, kinds);
        Assert.Contains(MetadataMediaKind.Movie, kinds);
    }
}
