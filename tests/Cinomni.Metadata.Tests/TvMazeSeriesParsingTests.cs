using System.Text.Json;
using Cinomni.Metadata.Contracts;
using Cinomni.Metadata.Providers;
using Cinomni.Metadata.Tests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cinomni.Metadata.Tests;

/// <summary>
/// Parser tests for the TVMaze adapter against scripted HTTP responses — no database, no network. TVMaze
/// is the provider that embeds the whole structure in one call and the only one publishing a genuinely
/// timezone-aware air time, so both are pinned here.
/// </summary>
public sealed class TvMazeSeriesParsingTests
{
    private const string BaseAddress = "https://tvmaze.example/";

    private readonly FakeHttpMessageHandler _handler = new FakeHttpMessageHandler()
        .Respond("shows/1/images", TvMazeFixtures.Images)
        .Respond("search/shows", TvMazeFixtures.Search)
        .Respond("shows/1", TvMazeFixtures.ShowWithEmbeds);

    private TvMazeMetadataSource CreateSource() =>
        new(_handler.CreateClient(BaseAddress), NullLogger<TvMazeMetadataSource>.Instance);

    [Fact]
    public async Task Parses_embedded_episodes_and_seasons()
    {
        var result = await CreateSource().FetchAsync(TvMazeFixtures.ShowId, MetadataMediaKind.Series);

        var series = result!.Series;
        Assert.NotNull(series);
        Assert.Equal([1, 2], series.Seasons.Select(s => s.Number));
        Assert.Equal("Season One", series.Seasons[0].Title);
        Assert.Equal(3, series.Seasons[0].EpisodeCount);
        Assert.Equal(new DateOnly(2024, 1, 8), series.Seasons[0].AirDate);
        Assert.Equal("https://images.example/frontier-s01.jpg", series.Seasons[0].PosterUrl);
        Assert.Null(series.Seasons[1].Title); // an empty name is not a title
        Assert.Null(series.Seasons[1].PosterUrl);

        // The unnumbered entry cannot be addressed by SxxEyy, so it is dropped rather than guessed at.
        Assert.Equal(
            [(0, 1), (1, 1), (1, 2), (1, 3)],
            series.Episodes.Select(e => (e.SeasonNumber, e.Number)).OrderBy(e => e).ToList());

        var premiere = series.Episodes.Single(e => e is { SeasonNumber: 1, Number: 1 });
        Assert.Equal("Departure", premiere.Title);
        Assert.Equal(42, premiere.RuntimeMinutes);
        Assert.Equal("https://images.example/frontier-s01e01.jpg", premiere.StillUrl);
        Assert.Equal("101", premiere.ExternalId);
    }

    [Fact]
    public async Task Maps_specials_by_type()
    {
        var result = await CreateSource().FetchAsync(TvMazeFixtures.ShowId, MetadataMediaKind.Series);
        var episodes = result!.Series!.Episodes;

        // A typed special inside a regular season, and the season-0 bucket, are both specials.
        Assert.True(episodes.Single(e => e is { SeasonNumber: 1, Number: 3 }).IsSpecial);
        Assert.True(episodes.Single(e => e.SeasonNumber == 0).IsSpecial);
        Assert.False(episodes.Single(e => e is { SeasonNumber: 1, Number: 1 }).IsSpecial);
    }

    [Fact]
    public async Task Strips_html_from_episode_summaries()
    {
        var result = await CreateSource().FetchAsync(TvMazeFixtures.ShowId, MetadataMediaKind.Series);

        Assert.Equal("A synthetic show used only by the tests.", result!.Overview);
        Assert.Equal("They leave at last.", result.Series!.Episodes.Single(e => e is { SeasonNumber: 1, Number: 1 }).Overview);
        Assert.Equal("The first season.", result.Series.Seasons[0].Overview);
    }

    [Fact]
    public async Task Reads_thetvdb_external_id()
    {
        var result = await CreateSource().FetchAsync(TvMazeFixtures.ShowId, MetadataMediaKind.Series);

        // TVMaze publishes TheTVDB's id as a number; everything else in the platform treats it as text.
        Assert.Equal("900001", result!.Series!.ExternalIds.TvdbId);
        Assert.Equal("tt9000001", result.Series.ExternalIds.ImdbId);
        Assert.Null(result.Series.ExternalIds.TmdbId);
    }

    [Fact]
    public async Task A_search_hit_carries_the_same_external_ids()
    {
        var candidates = await CreateSource().SearchAsync(new MetadataProviderQuery("frontier", null, MetadataMediaKind.Series));

        var candidate = Assert.Single(candidates);
        Assert.Equal("1", candidate.ExternalId);
        Assert.Equal(2024, candidate.Year);
        Assert.Equal("900001", candidate.ExternalIds!.TvdbId);
        Assert.Equal("tt9000001", candidate.ExternalIds.ImdbId);
    }

    [Fact]
    public async Task An_episode_keeps_the_published_date_and_the_timezone_aware_instant()
    {
        var result = await CreateSource().FetchAsync(TvMazeFixtures.ShowId, MetadataMediaKind.Series);

        var premiere = result!.Series!.Episodes.Single(e => e is { SeasonNumber: 1, Number: 1 });
        Assert.Equal(new DateOnly(2024, 1, 8), premiere.AirDate);
        // 21:00-05:00 is 02:00 UTC the following day: the two fields disagree on the calendar day on
        // purpose, and both are needed (date matching vs the unaired gate).
        Assert.Equal(new DateTimeOffset(2024, 1, 9, 2, 0, 0, TimeSpan.Zero), premiere.AirDateTime);
        Assert.Equal(TimeSpan.Zero, premiere.AirDateTime!.Value.Offset);
    }

    [Fact]
    public async Task The_status_and_the_show_dates_come_from_the_show()
    {
        var result = await CreateSource().FetchAsync(TvMazeFixtures.ShowId, MetadataMediaKind.Series);

        Assert.Equal(SeriesStatus.Continuing, result!.Series!.Status); // TVMaze spells it "Running"
        Assert.Equal(new DateOnly(2024, 1, 8), result.Series.FirstAired);
        Assert.Null(result.Series.LastAired);
        Assert.Equal(SeasonOrders.Official, result.Series.SeasonOrder);
        Assert.Equal(44, result.RuntimeMinutes); // averageRuntime wins over runtime
        Assert.Equal("en", result.OriginalLanguage);
    }

    [Fact]
    public async Task The_raw_response_drops_the_embedded_arrays()
    {
        var result = await CreateSource().FetchAsync(TvMazeFixtures.ShowId, MetadataMediaKind.Series);

        // raw_response is jsonb: keeping a 900-episode embed there stores the structure a second time,
        // per provider, per refresh. It must still be valid JSON with the scalar fields intact.
        using var raw = JsonDocument.Parse(result!.RawJson);
        Assert.False(raw.RootElement.TryGetProperty("_embedded", out _));
        Assert.Equal("Fake Frontier", raw.RootElement.GetProperty("name").GetString());
        Assert.DoesNotContain("Departure", result.RawJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Artwork_maps_the_tvmaze_taxonomy_and_ignores_banners()
    {
        var result = await CreateSource().FetchAsync(TvMazeFixtures.ShowId, MetadataMediaKind.Series);

        Assert.Equal("https://images.example/poster.jpg", result!.PosterUrl);
        Assert.Equal("https://images.example/background.jpg", result.BackdropUrl);
        Assert.Single(result.Artwork, a => a.Kind == ArtworkKind.Logo);
        // A "banner" is a wide strip, not a logo, and has no neutral kind — it is left out entirely.
        Assert.Equal(3, result.Artwork.Count);
        Assert.All(result.Artwork, a => Assert.Null(a.SeasonNumber));
    }

    [Fact]
    public async Task A_movie_fetch_is_refused()
    {
        var result = await CreateSource().FetchAsync(TvMazeFixtures.ShowId, MetadataMediaKind.Movie);

        Assert.Null(result);
        Assert.Empty(_handler.Requests); // TVMaze holds no movies — no request is even made
    }
}
