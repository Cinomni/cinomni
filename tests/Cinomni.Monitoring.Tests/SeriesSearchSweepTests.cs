using Cinomni.Catalog.Contracts;
using Cinomni.Monitoring.Application;
using Cinomni.Monitoring.Contracts;
using Cinomni.Monitoring.Persistence;
using Cinomni.Search.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Monitoring.Tests;

/// <summary>
/// The sweep is where series scale becomes a correctness problem rather than a performance one: every
/// <c>SearchRequested</c> fans out to every enabled indexer, and real indexers rate-limit or ban. These
/// facts pin the per-work quota, the season-vs-episode granularity, the shared cooldown stamp that stops a
/// pack search and its episodes racing for the same file, and the unaired gate.
/// </summary>
public sealed class SeriesSearchSweepTests : IAsyncLifetime
{
    private static readonly DateOnly LongAgo = new(2012, 3, 1);
    private static readonly DateOnly NotYet = new(2999, 1, 1);

    private readonly MonitoringEventSink _sink = new();
    private ServiceProvider _host = null!;
    private MonitoringDriver _driver = null!;

    public async Task InitializeAsync()
    {
        _host = await MonitoringTestHost.CreateAsync(
            "cinomni_test_monitoring_sweep", MonitoringEventSink.Register(_sink));
        _driver = new MonitoringDriver(_host);
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task A_mostly_missing_season_requests_one_season_search()
    {
        var workId = await BuildSeriesAsync("Justified", episodesInSeasonOne: 10);

        await SweepAsync();

        // Ten separate requests would have been ten fan-outs to every indexer for the same pack.
        var search = Assert.Single(_sink.Searches);
        var season = await SeasonAsync(workId, 1);
        Assert.Equal(season.Id, search.TargetId);
        Assert.Equal(TargetKind.Season.ToString(), search.Criterion.ContentKind);
        Assert.Equal(1, search.Criterion.SeasonNumber);
        Assert.Null(search.Criterion.EpisodeNumber);
        // The units are the episodes still missing, so Decision can rank a pack by what it covers.
        Assert.Equal(10, search.UnitIds!.Count);
    }

    [Fact]
    public async Task A_season_search_suppresses_its_episode_searches_for_the_cooldown()
    {
        var workId = await BuildSeriesAsync("Justified", episodesInSeasonOne: 10);

        await SweepAsync();

        // The stamp lands on the season AND on every episode inside it — that single column is the whole
        // duplicate-download suppressor.
        var targets = await _driver.TargetsAsync(workId);
        Assert.All(
            targets.Where(t => t.Kind is TargetKind.Season or TargetKind.Episode),
            target => Assert.NotNull(target.LastSearchRequestedAt));

        await SweepAsync();
        Assert.Single(_sink.Searches);

        // Once the cooldown lapses the pack is asked for again — still one request, not ten.
        await _driver.RewindSearchStampsAsync(workId, TimeSpan.FromHours(7));
        await SweepAsync();
        Assert.Equal(2, _sink.Searches.Count);
        Assert.All(_sink.Searches, s => Assert.Equal(TargetKind.Season.ToString(), s.Criterion.ContentKind));
    }

    [Fact]
    public async Task A_sparsely_missing_season_requests_per_episode_searches()
    {
        var workId = await BuildSeriesAsync("Justified", episodesInSeasonOne: 10);

        // Eight of ten already on disk: 2 missing is below both the 60% ratio and the 3-episode floor.
        await LandEpisodesAsync(workId, 1, Enumerable.Range(1, 8));

        await SweepAsync();

        Assert.Equal(2, _sink.Searches.Count);
        Assert.All(_sink.Searches, s => Assert.Equal(TargetKind.Episode.ToString(), s.Criterion.ContentKind));
        Assert.Equal([9, 10], _sink.Searches.Select(s => s.Criterion.EpisodeNumber!.Value).Order().ToList());
    }

    [Fact]
    public async Task One_large_series_cannot_consume_the_whole_sweep_quota()
    {
        // A season of 40 with 12 missing is sparse (30%), so it plans per-episode requests — more than the
        // per-work quota allows.
        var seriesId = await BuildSeriesAsync("The Simpsons", episodesInSeasonOne: 40);
        await LandEpisodesAsync(seriesId, 1, Enumerable.Range(1, 28));

        var movieId = await _driver.AddMovieAsync("Heat", 1995);
        await _driver.DrainAsync();

        await SweepAsync();

        var perWork = _sink.Searches.GroupBy(s => s.WorkId).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(SearchGranularityPolicy.MaxSearchesPerWork, perWork[seriesId.Value]);
        // ...and the movie behind it is still served on the very same sweep.
        Assert.Equal(1, perWork[movieId.Value]);
    }

    [Fact]
    public async Task An_unaired_episode_is_never_searched()
    {
        var workId = await _driver.AddSeriesAsync("Severance", 2022);
        await _driver.DrainAsync();
        await _driver.SyncStructureAsync(
            workId,
            Guid.NewGuid(),
            [new SeasonStructureInput(1)],
            [
                new EpisodeStructureInput(1, 1, "Good News", AirDate: LongAgo),
                new EpisodeStructureInput(1, 2, "Half Loop", AirDate: NotYet),
            ]);
        await _driver.DrainAsync();

        await SweepAsync();

        // The unaired episode holds a monitored, missing target — it is simply never asked for, so its
        // acquisition intent never burns an attempt on content that does not exist yet.
        var unaired = await EpisodeAsync(workId, 1, 2);
        Assert.True(unaired is { Monitored: true, IsMissing: true });

        var search = Assert.Single(_sink.Searches);
        Assert.Equal(1, search.Criterion.EpisodeNumber);
        Assert.Null(unaired.LastSearchRequestedAt);
    }

    [Fact]
    public async Task A_just_aired_episode_uses_the_short_cooldown()
    {
        var recentId = await OneEpisodeSeriesAsync(
            "Tonight", DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-1)));
        var oldId = await OneEpisodeSeriesAsync("Yesteryear", LongAgo);

        await SweepAsync();
        Assert.Equal(2, _sink.Searches.Count);

        // Forty-five minutes later: past the 30-minute recent-air cooldown, far short of the flat six hours.
        await _driver.RewindSearchStampsAsync(recentId, TimeSpan.FromMinutes(45));
        await _driver.RewindSearchStampsAsync(oldId, TimeSpan.FromMinutes(45));
        await SweepAsync();

        Assert.Equal(2, _sink.Searches.Count(s => s.WorkId == recentId.Value));
        Assert.Equal(1, _sink.Searches.Count(s => s.WorkId == oldId.Value));
    }

    [Fact]
    public async Task The_criterion_carries_the_series_title_and_the_episode_numbers()
    {
        var workId = await _driver.AddSeriesAsync(
            "The Wire",
            2002,
            [new ExternalId(MetadataProvider.Tvdb, "79126"), new ExternalId(MetadataProvider.Imdb, "tt0306414")]);
        await _driver.DrainAsync();
        await _driver.SyncStructureAsync(
            workId,
            Guid.NewGuid(),
            [new SeasonStructureInput(2)],
            [new EpisodeStructureInput(2, 5, "Undertow", AbsoluteNumber: 18, AirDate: LongAgo)]);
        await _driver.DrainAsync();

        await SweepAsync();

        var search = Assert.Single(_sink.Searches);
        // Term stays the SERIES title: an indexer is queried with season/ep parameters, never with
        // "The Wire S02E05" as a free-text term.
        Assert.Equal("The Wire", search.Criterion.Term);
        Assert.Equal(TargetKind.Episode.ToString(), search.Criterion.ContentKind);
        Assert.Equal(2, search.Criterion.SeasonNumber);
        Assert.Equal(5, search.Criterion.EpisodeNumber);
        Assert.Equal(18, search.Criterion.AbsoluteNumber);
        Assert.Equal(LongAgo, search.Criterion.AirDate);
        // The movie-era criterion extracted only IMDb and TMDB; TVDB is the id TV indexers key on.
        Assert.Equal("79126", search.Criterion.TvdbId);
        Assert.Equal("tt0306414", search.Criterion.ImdbId);
    }

    [Fact]
    public async Task A_tz_aware_air_instant_still_searches_the_published_air_date()
    {
        // TVMaze publishes an airstamp, not just a date: 23:00 on the 10th in US Eastern is 04:00 on the
        // 11th in UTC. The *published* date is the key that date-based release matching compares
        // against, so re-deriving it from the tz-aware gate instant asks the indexer for the wrong night
        // and then rejects the right release when it arrives.
        var published = new DateOnly(2024, 3, 10);
        var workId = await _driver.AddSeriesAsync("The Nightly Show", 2015);
        await _driver.DrainAsync();
        await _driver.SyncStructureAsync(
            workId,
            Guid.NewGuid(),
            [new SeasonStructureInput(1)],
            [
                new EpisodeStructureInput(
                    1,
                    1,
                    "Monday",
                    AirDate: published,
                    AirDateTime: new DateTimeOffset(2024, 3, 10, 23, 0, 0, TimeSpan.FromHours(-4))),
            ]);
        await _driver.DrainAsync();

        await SweepAsync();

        var search = Assert.Single(_sink.Searches);
        Assert.Equal(published, search.Criterion.AirDate);
    }

    [Fact]
    public async Task Two_search_occasions_for_one_target_carry_different_windows()
    {
        var workId = await _driver.AddMovieAsync("Dune", 2021);
        await _driver.DrainAsync();

        await SweepAsync();
        // The cooldown lapses without the wall clock leaving its hour — which is precisely what the
        // manual season-search endpoint arranges on purpose.
        await _driver.RewindSearchStampsAsync(workId, TimeSpan.FromHours(7));
        await SweepAsync();

        Assert.Equal(2, _sink.Searches.Count);
        // An hour bucket gave both occasions the same window, so the consumer's
        // execute-search:{target}:{window} key — spent for ever — dropped the second one silently.
        Assert.Equal(2, _sink.Searches.Select(s => s.Window).Distinct().Count());
        Assert.Equal(2, _sink.Searches.Select(s => s.IdempotencyKey).Distinct().Count());
    }

    [Fact]
    public async Task A_manually_cleared_season_cooldown_produces_a_second_distinct_search()
    {
        var workId = await BuildSeriesAsync("Justified", episodesInSeasonOne: 10);

        await SweepAsync();
        Assert.Single(_sink.Searches);

        // Exactly what POST /works/{id}/seasons/{n}/search does: forget the season's stamps, re-run the
        // sweep — minutes later, still inside the same clock hour. An hour-bucketed window made the second
        // request collide with the first on the consumer's key, so the endpoint that exists to force a
        // search out of band could not force one, and re-stamped the cooldown it had just cleared.
        await _driver.ClearSeasonCooldownAsync(workId, 1);
        await SweepAsync();

        Assert.Equal(2, _sink.Searches.Count);
        Assert.Equal(2, _sink.Searches.Select(s => s.Window).Distinct().Count());
    }

    [Fact]
    public async Task A_movie_sweep_is_unchanged()
    {
        var workId = await _driver.AddMovieAsync("Dune", 2021);
        await _driver.DrainAsync();

        await SweepAsync();

        var search = Assert.Single(_sink.Searches);
        Assert.Equal("Dune", search.Criterion.Term);
        Assert.Equal(TargetKind.Movie.ToString(), search.Criterion.ContentKind);
        Assert.Null(search.Criterion.SeasonNumber);
        Assert.Null(search.Criterion.EpisodeNumber);
        Assert.Null(search.Criterion.AirDate);
        Assert.Equal(workId.Value, Assert.Single(search.UnitIds!));

        // ...and the flat six-hour cooldown still holds.
        await SweepAsync();
        Assert.Single(_sink.Searches);
    }

    // -- fixture ---------------------------------------------------------------------------------

    private async Task<WorkId> BuildSeriesAsync(string title, int episodesInSeasonOne)
    {
        var workId = await _driver.AddSeriesAsync(title, 2010);
        await _driver.DrainAsync();
        await _driver.SyncStructureAsync(
            workId,
            Guid.NewGuid(),
            [new SeasonStructureInput(1)],
            Enumerable.Range(1, episodesInSeasonOne)
                .Select(n => new EpisodeStructureInput(1, n, $"Episode {n}", AirDate: LongAgo))
                .ToList());
        await _driver.DrainAsync();
        return workId;
    }

    private async Task<WorkId> OneEpisodeSeriesAsync(string title, DateOnly airDate)
    {
        var workId = await _driver.AddSeriesAsync(title, 2020);
        await _driver.DrainAsync();
        await _driver.SyncStructureAsync(
            workId,
            Guid.NewGuid(),
            [new SeasonStructureInput(1)],
            [new EpisodeStructureInput(1, 1, "Pilot", AirDate: airDate)]);
        await _driver.DrainAsync();
        return workId;
    }

    /// <summary>Lands one asset per episode, exactly as N single-episode imports would.</summary>
    private async Task LandEpisodesAsync(WorkId workId, int seasonNumber, IEnumerable<int> episodeNumbers)
    {
        var targets = await _driver.TargetsAsync(workId);
        var wanted = episodeNumbers.ToHashSet();
        var units = targets
            .Where(t => t.Kind == TargetKind.Episode
                && t.SeasonNumber == seasonNumber
                && wanted.Contains(t.EpisodeNumber ?? 0))
            .Select(t => t.TargetRef)
            .ToList();

        await _driver.LandAssetAsync(workId, units);
    }

    private async Task SweepAsync()
    {
        await _driver.RunEvaluateMissingAsync();
        await _driver.DrainAsync();
    }

    private async Task<MonitoredTarget> SeasonAsync(WorkId workId, int number) =>
        (await _driver.TargetsAsync(workId)).Single(t => t.Kind == TargetKind.Season && t.SeasonNumber == number);

    private async Task<MonitoredTarget> EpisodeAsync(WorkId workId, int season, int number) =>
        (await _driver.TargetsAsync(workId))
        .Single(t => t.Kind == TargetKind.Episode && t.SeasonNumber == season && t.EpisodeNumber == number);
}
