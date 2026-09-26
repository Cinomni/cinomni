using System.Text.Json;
using Cinomni.Metadata.Contracts;
using Cinomni.Metadata.Providers;
using Cinomni.Metadata.Tests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cinomni.Metadata.Tests;

/// <summary>
/// Parser tests for TheTVDB adapter against scripted HTTP responses — no database, no network. This is
/// the provider that pages its episodes, publishes several season orderings, and is the platform's only
/// source of absolute (anime) numbering, so all three are pinned here.
/// </summary>
public sealed class TvdbSeriesParsingTests
{
    private const string BaseAddress = "https://tvdb.example/";

    private readonly FakeHttpMessageHandler _handler = new();

    private TvdbMetadataSource CreateSource(Action<TvdbProviderOptions>? configure = null)
    {
        var options = new TvdbProviderOptions { ApiKey = "fake-key", BaseAddress = BaseAddress };
        configure?.Invoke(options);

        var tokenProvider = new TvdbTokenProvider(
            new SingleClientFactory(_handler, BaseAddress),
            options,
            new DisabledProviderNotice(NullLogger<DisabledProviderNotice>.Instance),
            NullLogger<TvdbTokenProvider>.Instance);

        return new TvdbMetadataSource(
            _handler.CreateClient(BaseAddress),
            tokenProvider,
            options,
            NullLogger<TvdbMetadataSource>.Instance);
    }

    private void ScriptOfficialSeries(string status = "Continuing") =>
        _handler
            .Respond("login", TvdbFixtures.Login)
            .Respond("search?query", TvdbFixtures.Search)
            .Respond("series/2/extended", TvdbFixtures.SeriesExtended(status))
            .Respond("episodes/official?page=0", TvdbFixtures.EpisodesPage0)
            .Respond("episodes/official?page=1", TvdbFixtures.EpisodesPage1);

    [Fact]
    public async Task A_rejected_token_is_replaced_and_the_request_sent_again()
    {
        // The token is cached for 25 days; a key rotated or a token revoked early used to fail every
        // call until then.
        ScriptOfficialSeries();
        _handler.RespondOnceWithStatus("series/2/extended", System.Net.HttpStatusCode.Unauthorized);

        var result = await CreateSource().FetchAsync(TvdbFixtures.SeriesId, MetadataMediaKind.Series);

        Assert.NotNull(result);
        Assert.Equal(2, _handler.CountOf("login"));
        Assert.Equal(2, _handler.CountOf("series/2/extended"));
        // The renewed token reaches the episode pages too: each is asked for once, not refused first.
        Assert.Equal(2, _handler.CountOf("episodes/official"));
    }

    [Fact]
    public async Task A_token_rejected_twice_is_a_failure_not_a_loop()
    {
        ScriptOfficialSeries();
        _handler
            .RespondOnceWithStatus("series/2/extended", System.Net.HttpStatusCode.Unauthorized)
            .RespondOnceWithStatus("series/2/extended", System.Net.HttpStatusCode.Unauthorized);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => CreateSource().FetchAsync(TvdbFixtures.SeriesId, MetadataMediaKind.Series));

        Assert.Equal(2, _handler.CountOf("series/2/extended"));
    }

    [Fact]
    public async Task Parses_extended_series_with_seasons()
    {
        ScriptOfficialSeries();

        var result = await CreateSource().FetchAsync(TvdbFixtures.SeriesId, MetadataMediaKind.Series);

        Assert.Equal("Fake Frontier", result!.Title);
        Assert.Equal(2024, result.Year);
        Assert.Equal(44, result.RuntimeMinutes); // averageRuntime, not the absent series "runtime"
        Assert.Equal("en", result.OriginalLanguage);

        var series = result.Series;
        Assert.NotNull(series);
        Assert.Equal(new DateOnly(2024, 1, 8), series.FirstAired);
        Assert.Equal(new DateOnly(2025, 2, 3), series.LastAired);
        Assert.Equal(SeasonOrders.Official, series.SeasonOrder);

        // The record lists a DVD season too; only the configured ordering defines our numbering.
        Assert.Equal([1, 2], series.Seasons.Select(s => s.Number));
        Assert.Equal("https://artworks.example/official-s01.jpg", series.Seasons[0].PosterUrl);
        Assert.Equal("Season One", series.Seasons[0].Title);
    }

    [Fact]
    public async Task Follows_episode_pagination()
    {
        ScriptOfficialSeries();

        var result = await CreateSource().FetchAsync(TvdbFixtures.SeriesId, MetadataMediaKind.Series);

        // Two pages of two, and the walk stops when links.next is null — not one page, not three.
        Assert.Equal(4, result!.Series!.Episodes.Count);
        Assert.Equal(2, _handler.CountOf("episodes/official?page="));
        Assert.Contains(result.Series.Episodes, e => e is { SeasonNumber: 2, Number: 1 });
        Assert.Contains(result.Series.Episodes, e => e is { SeasonNumber: 0, Number: 1 });
    }

    [Fact]
    public async Task The_episode_page_walk_is_capped()
    {
        _handler
            .Respond("login", TvdbFixtures.Login)
            .Respond("series/2/extended", TvdbFixtures.SeriesExtended())
            // Every page claims another one follows: without the cap this never terminates.
            .Respond("episodes/official", TvdbFixtures.EndlessEpisodesPage(2));

        await CreateSource(options => options.MaxEpisodePages = 3).FetchAsync(TvdbFixtures.SeriesId, MetadataMediaKind.Series);

        Assert.Equal(3, _handler.CountOf("episodes/official?page="));
    }

    [Fact]
    public async Task Reads_absolute_numbers()
    {
        ScriptOfficialSeries();

        var result = await CreateSource().FetchAsync(TvdbFixtures.SeriesId, MetadataMediaKind.Series);
        var episodes = result!.Series!.Episodes;

        Assert.Equal(1, episodes.Single(e => e is { SeasonNumber: 1, Number: 1 }).AbsoluteNumber);
        Assert.Equal(3, episodes.Single(e => e is { SeasonNumber: 2, Number: 1 }).AbsoluteNumber);
        // Most non-anime specials carry none, which is why the absolute index is filtered, not unique.
        Assert.Null(episodes.Single(e => e.SeasonNumber == 0).AbsoluteNumber);
    }

    [Theory]
    [InlineData("Continuing", SeriesStatus.Continuing)]
    [InlineData("Ended", SeriesStatus.Ended)]
    [InlineData("Cancelled", SeriesStatus.Cancelled)]
    [InlineData("Upcoming", SeriesStatus.Upcoming)]
    [InlineData("Something The Provider Invented", SeriesStatus.Unknown)]
    public async Task Maps_status_to_series_status(string provided, SeriesStatus expected)
    {
        ScriptOfficialSeries(provided);

        var result = await CreateSource().FetchAsync(TvdbFixtures.SeriesId, MetadataMediaKind.Series);

        Assert.Equal(expected, result!.Series!.Status);
    }

    [Fact]
    public async Task The_configured_season_type_decides_which_ordering_is_fetched()
    {
        _handler
            .Respond("login", TvdbFixtures.Login)
            .Respond("series/2/extended", TvdbFixtures.SeriesExtended())
            .Respond("episodes/dvd", TvdbFixtures.DvdEpisodesPage0);

        var result = await CreateSource(options => options.SeasonType = SeasonOrders.Dvd)
            .FetchAsync(TvdbFixtures.SeriesId, MetadataMediaKind.Series);

        // The DVD ordering renumbers the same broadcasts; recording which one produced the numbers is
        // what makes a later snapshot under another ordering recognisable.
        Assert.Equal(SeasonOrders.Dvd, result!.Series!.SeasonOrder);
        Assert.Equal(1, _handler.CountOf("episodes/dvd"));
        Assert.Equal(0, _handler.CountOf("episodes/official"));
        Assert.Equal([1], result.Series.Seasons.Select(s => s.Number));
        Assert.Equal(2, result.Series.Episodes.Single(e => e.ExternalId == "401").Number);
    }

    [Fact]
    public async Task An_unknown_season_type_falls_back_to_the_official_ordering()
    {
        ScriptOfficialSeries();

        var result = await CreateSource(options => options.SeasonType = "whatever-the-operator-typed")
            .FetchAsync(TvdbFixtures.SeriesId, MetadataMediaKind.Series);

        Assert.Equal(SeasonOrders.Official, result!.Series!.SeasonOrder);
        Assert.Equal(2, _handler.CountOf("episodes/official?page="));
    }

    [Fact]
    public async Task Reads_the_remote_ids_of_the_extended_record_and_of_a_search_hit()
    {
        ScriptOfficialSeries();

        var result = await CreateSource().FetchAsync(TvdbFixtures.SeriesId, MetadataMediaKind.Series);
        Assert.Equal("2", result!.Series!.ExternalIds.TvdbId);
        Assert.Equal("tt9000001", result.Series.ExternalIds.ImdbId);
        Assert.Equal("777", result.Series.ExternalIds.TmdbId);

        var candidate = Assert.Single(
            await CreateSource().SearchAsync(new MetadataProviderQuery("frontier", null, MetadataMediaKind.Series)));
        Assert.Equal("2", candidate.ExternalIds!.TvdbId);
        Assert.Equal("tt9000001", candidate.ExternalIds.ImdbId);
        Assert.Equal("777", candidate.ExternalIds.TmdbId);
    }

    [Fact]
    public async Task The_raw_response_drops_the_bulky_arrays()
    {
        ScriptOfficialSeries();

        var result = await CreateSource().FetchAsync(TvdbFixtures.SeriesId, MetadataMediaKind.Series);

        using var raw = JsonDocument.Parse(result!.RawJson);
        Assert.False(raw.RootElement.TryGetProperty("characters", out _));
        Assert.False(raw.RootElement.TryGetProperty("artworks", out _));
        // The scalars a human would want when debugging a snapshot survive.
        Assert.Equal("Fake Frontier", raw.RootElement.GetProperty("name").GetString());
        Assert.True(raw.RootElement.TryGetProperty("seasons", out _));
    }

    [Fact]
    public async Task Artwork_uses_the_series_type_map()
    {
        ScriptOfficialSeries();

        var result = await CreateSource().FetchAsync(TvdbFixtures.SeriesId, MetadataMediaKind.Series);

        Assert.Equal("https://artworks.example/poster.jpg", result!.PosterUrl);
        Assert.Equal("https://artworks.example/background.jpg", result.BackdropUrl);
        Assert.Equal("en", result.Artwork.Single(a => a.Kind == ArtworkKind.Poster).Language);
    }

    [Fact]
    public async Task Without_a_token_nothing_is_fetched()
    {
        ScriptOfficialSeries();

        var result = await CreateSource(options => options.ApiKey = string.Empty)
            .FetchAsync(TvdbFixtures.SeriesId, MetadataMediaKind.Series);

        Assert.Null(result);
        Assert.Empty(_handler.Requests);
    }

    /// <summary>Hands the token provider the same scripted handler the data adapter uses.</summary>
    private sealed class SingleClientFactory(FakeHttpMessageHandler handler, string baseAddress) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => handler.CreateClient(baseAddress);
    }
}
