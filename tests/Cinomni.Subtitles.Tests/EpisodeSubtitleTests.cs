using System.Diagnostics;
using Cinomni.Catalog.Contracts;
using Cinomni.Kernel.Identifiers;
using Cinomni.Library.Contracts;
using Cinomni.Subtitles.Application;
using Cinomni.Subtitles.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Subtitles.Tests;

/// <summary>
/// Episode awareness and burst control for the subtitle search, against a real PostgreSQL instance:
/// an episode asset resolves its catalog units to <c>SxxEyy</c> before it queries a provider, a movie
/// carries no numbering at all, and a season pack's N registrations are staggered instead of fired as
/// one N-fold burst at a rate-limited API.
/// </summary>
public sealed class EpisodeSubtitleTests : IAsyncLifetime
{
    private const string SeriesTitle = "The Wire";
    private const string ImdbId = "tt0306414";
    private const int SeasonNumber = 2;
    private const int EpisodeCount = 24;

    /// <summary>Small enough to keep the suite fast, large enough to dominate scheduling noise.</summary>
    private static readonly TimeSpan Stagger = TimeSpan.FromMilliseconds(20);

    private readonly FakeSubtitleProvider _provider = new();
    private readonly FakeSubtitleFileStore _fileStore = new();
    private ServiceProvider _host = null!;
    private WorkId _workId;
    private IReadOnlyList<EpisodeSummary> _episodes = [];

    public async Task InitializeAsync()
    {
        _host = await SubtitlesTestHost.CreateAsync(
            "cinomni_test_subtitles_series",
            _provider,
            _fileStore,
            new SubtitleOptions
            {
                WantedLanguages = ["en"],
                MinScore = 5,
                ProviderCallInterval = Stagger,
            });

        await using var scope = _host.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<ICatalogCommands>();
        _workId = (await catalog.AddSeriesAsync(
            SeriesTitle,
            2002,
            [new ExternalId(MetadataProvider.Tvdb, "79126"), new ExternalId(MetadataProvider.Imdb, ImdbId)])).Value;

        await catalog.SyncSeriesStructureAsync(_workId.Value, new SeriesStructure(
            Uuid7.New(),
            "tvdb",
            [new SeasonStructureInput(SeasonNumber)],
            [.. Enumerable.Range(1, EpisodeCount).Select(n => new EpisodeStructureInput(SeasonNumber, n, $"Episode {n}"))]));

        _episodes = await scope.ServiceProvider.GetRequiredService<ICatalogSeriesQuery>()
            .GetEpisodesAsync(_workId, SeasonNumber);
        Assert.Equal(EpisodeCount, _episodes.Count);
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task An_episode_query_carries_season_and_episode_numbers()
    {
        // Arrange — one file that plays S02E05, registered with that episode as its catalog unit.
        var episode = _episodes[4];
        var assetId = await RegisterEpisodeAssetAsync(episode);

        // Act
        await SearchAsync(assetId);

        // Assert — the provider was told exactly which episode to look for, plus the series identity.
        var query = Assert.Single(_provider.Searches);
        Assert.Equal(SeasonNumber, query.SeasonNumber);
        Assert.Equal(5, query.EpisodeNumber);
        Assert.Equal(SeriesTitle, query.SeriesTitle);
        Assert.Equal(ImdbId, query.ParentImdbId);
        Assert.True(query.IsEpisode);
    }

    [Fact]
    public async Task A_movie_query_carries_neither()
    {
        // Arrange — the movie path: the asset's only unit is its own work.
        var movieWorkId = await AddMovieAsync();
        var assetId = Uuid7.New();
        await RegisterAsync(assetId, movieWorkId, "/data/library/Movie.2024/Movie.2024.mkv", [movieWorkId]);

        // Act
        await SearchAsync(assetId);

        // Assert — nothing series-shaped leaks into a movie search.
        var query = Assert.Single(_provider.Searches);
        Assert.Null(query.SeasonNumber);
        Assert.Null(query.EpisodeNumber);
        Assert.Null(query.SeriesTitle);
        Assert.Null(query.ParentImdbId);
        Assert.False(query.IsEpisode);
        Assert.Equal("Movie.2024", query.Release);
    }

    [Fact]
    public async Task A_season_pack_registration_staggers_its_provider_calls()
    {
        // Arrange — a 24-episode pack lands 24 assets, each of which fires its own search.
        var assetIds = new List<Guid>();
        foreach (var episode in _episodes)
        {
            assetIds.Add(await RegisterEpisodeAssetAsync(episode));
        }

        // Act — the worst case: every search is triggered at once, from its own scope.
        var elapsed = Stopwatch.StartNew();
        await Task.WhenAll(assetIds.Select(SearchAsync));
        elapsed.Stop();

        // Assert — all 24 searches happened, but they could not have burst: the throttle holds one
        // call at a time and one interval apart, so N calls take at least (N-1) intervals.
        Assert.Equal(EpisodeCount, _provider.Searches.Count);
        Assert.True(
            elapsed.Elapsed >= Stagger * (EpisodeCount - 1),
            $"24 searches finished in {elapsed.ElapsedMilliseconds} ms, faster than the {Stagger.TotalMilliseconds} ms stagger allows.");

        // ...and each one still asked for its own episode, so the stagger did not blur the queries.
        Assert.Equal(
            [.. Enumerable.Range(1, EpisodeCount)],
            [.. _provider.Searches.Select(s => s.EpisodeNumber!.Value).Order()]);
    }

    // -- helpers ---------------------------------------------------------------------------------

    private async Task<Guid> AddMovieAsync()
    {
        await using var scope = _host.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<ICatalogCommands>()
            .AddMovieAsync("Movie", 2024, [new ExternalId(MetadataProvider.Tmdb, "999")]);
        return result.Value.Value;
    }

    private async Task<Guid> RegisterEpisodeAssetAsync(EpisodeSummary episode)
    {
        var assetId = Uuid7.New();
        var path = $"/data/library/{SeriesTitle} (2002)/Season {SeasonNumber:00}/" +
                   $"{SeriesTitle} - S{episode.SeasonNumber:00}E{episode.Number:00}.mkv";
        await RegisterAsync(assetId, _workId.Value, path, [episode.Id.Value]);
        return assetId;
    }

    private async Task RegisterAsync(Guid assetId, Guid workId, string path, IReadOnlyList<Guid> unitIds)
    {
        var request = new RegisterMediaAssetRequest(
            assetId,
            workId,
            TargetIds: [Uuid7.New()],
            FullPath: path,
            Size: 2_000_000_000,
            Container: "matroska",
            Streams:
            [
                new MediaStreamInput(0, MediaStreamType.Video, "h264", null, null, 1920, 1080, null, null, true, false),
                new MediaStreamInput(1, MediaStreamType.Audio, "aac", "eng", 6, null, null, null, null, true, false),
            ],
            UnitIds: unitIds);

        await using var scope = _host.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ILibraryCommands>().RegisterMediaAssetAsync(request);
    }

    private async Task SearchAsync(Guid assetId)
    {
        await using var scope = _host.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ISubtitleSearch>().SearchForAssetAsync(assetId);
    }
}
