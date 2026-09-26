using Cinomni.Catalog.Contracts;
using Cinomni.Kernel.Identifiers;
using Cinomni.Kernel.Security;
using Cinomni.Library.Contracts;
using Cinomni.Playback.Contracts;
using Cinomni.Playback.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Playback.Tests;

/// <summary>
/// Series-aware playback progress against a real PostgreSQL instance: a progress row records the work
/// and unit it belongs to, a whole season's watched flags come back in one call, and "continue
/// watching" resolves the first unwatched episode that actually has a file — while a movie has no
/// "next" at all.
/// </summary>
public sealed class SeriesProgressTests : IAsyncLifetime
{
    private const string SeriesTitle = "The Wire";
    private const int SeasonNumber = 1;
    private const int EpisodeCount = 3;
    private const long Duration = 1000;

    private readonly FakeMediaEncoder _encoder = new();
    private ServiceProvider _provider = null!;
    private WorkId _workId;
    private IReadOnlyList<EpisodeSummary> _episodes = [];

    public async Task InitializeAsync()
    {
        _provider = await PlaybackTestHost.CreateAsync("cinomni_test_playback_series", _encoder);

        await using var scope = _provider.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<ICatalogCommands>();
        _workId = (await catalog.AddSeriesAsync(
            SeriesTitle, 2002, [new ExternalId(MetadataProvider.Tvdb, "79126")])).Value;

        await catalog.SyncSeriesStructureAsync(_workId.Value, new SeriesStructure(
            Uuid7.New(),
            "tvdb",
            [new SeasonStructureInput(SeasonNumber)],
            [.. Enumerable.Range(1, EpisodeCount).Select(n => new EpisodeStructureInput(SeasonNumber, n, $"Episode {n}"))]));

        _episodes = await scope.ServiceProvider.GetRequiredService<ICatalogSeriesQuery>()
            .GetEpisodesAsync(_workId, SeasonNumber);
        Assert.Equal(EpisodeCount, _episodes.Count);
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task Progress_records_the_work_id()
    {
        // Arrange
        var assetId = await RegisterEpisodeAssetAsync(_episodes[0]);
        var userId = Uuid7.New();

        // Act
        await WatchAsync(userId, assetId, positionTicks: 400);

        // Assert — the row carries the catalog correlation, so "continue watching" never has to join
        // Library row by row.
        await using var scope = _provider.CreateAsyncScope();
        var row = await scope.ServiceProvider.GetRequiredService<PlaybackDbContext>().Progress
            .AsNoTracking()
            .SingleAsync(p => p.UserId == userId && p.AssetId == assetId);
        Assert.Equal(_workId.Value, row.WorkId);
        Assert.Equal(_episodes[0].Id.Value, row.UnitId);
    }

    [Fact]
    public async Task Progress_for_a_season_returns_every_asset_in_one_call()
    {
        // Arrange — three episodes, two of them watched to different depths.
        var userId = Uuid7.New();
        var assetIds = new List<Guid>();
        foreach (var episode in _episodes)
        {
            assetIds.Add(await RegisterEpisodeAssetAsync(episode));
        }

        await WatchAsync(userId, assetIds[0], positionTicks: 960); // past the watched threshold
        await WatchAsync(userId, assetIds[1], positionTicks: 400);

        // Act
        var progress = await QueryAsync(q => q.GetProgressForAssetsAsync(userId, assetIds));

        // Assert — one call, one row per asset that has progress, each mapped to its episode.
        Assert.Equal(2, progress.Count);
        var watched = Assert.Single(progress, p => p.AssetId == assetIds[0]);
        Assert.True(watched.Played);
        Assert.Equal(_episodes[0].Id.Value, watched.UnitId);
        var partial = Assert.Single(progress, p => p.AssetId == assetIds[1]);
        Assert.False(partial.Played);
        Assert.Equal(400, partial.PositionTicks);
        Assert.DoesNotContain(progress, p => p.AssetId == assetIds[2]);
    }

    [Fact]
    public async Task Next_up_returns_the_first_unwatched_episode()
    {
        // Arrange — every episode has a file; episode 1 is watched, episode 2 is half-way in.
        var userId = Uuid7.New();
        var assetIds = new List<Guid>();
        foreach (var episode in _episodes)
        {
            assetIds.Add(await RegisterEpisodeAssetAsync(episode));
        }

        await WatchAsync(userId, assetIds[0], positionTicks: 960);
        await WatchAsync(userId, assetIds[1], positionTicks: 400);

        // Act
        var nextUp = await QueryAsync(q => q.GetNextUpAsync(new Viewer(userId, IsAdministrator: false), _workId.Value));

        // Assert — E02, not E01 (watched) and not E03 (further along the order), resuming where the
        // user left off.
        Assert.NotNull(nextUp);
        Assert.Equal(_episodes[1].Id.Value, nextUp.UnitId);
        Assert.Equal(assetIds[1], nextUp.AssetId);
        Assert.Equal(SeasonNumber, nextUp.SeasonNumber);
        Assert.Equal(2, nextUp.EpisodeNumber);
        Assert.Equal(400, nextUp.ResumePositionTicks);

        // ...and once it is watched too, the next unwatched episode takes over.
        await WatchAsync(userId, assetIds[1], positionTicks: 960);
        var after = await QueryAsync(q => q.GetNextUpAsync(new Viewer(userId, IsAdministrator: false), _workId.Value));
        Assert.Equal(3, after!.EpisodeNumber);
    }

    [Fact]
    public async Task Next_up_says_nothing_about_a_series_the_viewer_may_not_see()
    {
        // Arrange — a playable episode, on a shelf the member holds no grant for.
        var member = new Viewer(Uuid7.New(), IsAdministrator: false);
        var assetId = await RegisterEpisodeAssetAsync(_episodes[0]);
        await WatchAsync(member.UserId, assetId, positionTicks: 400);
        await using (var scope = _provider.CreateAsyncScope())
        {
            var collections = scope.ServiceProvider.GetRequiredService<ICollectionAdministration>();
            var shelf = await collections.CreateAsync("Grown-ups", CollectionKind.Series, CollectionAccessMode.Restricted);
            Assert.True((await collections.MoveWorkAsync(_workId, shelf.Value)).IsSuccess);
        }

        // Act
        var hidden = await QueryAsync(q => q.GetNextUpAsync(member, _workId.Value));
        var forOperator = await QueryAsync(q => q.GetNextUpAsync(new Viewer(Uuid7.New(), IsAdministrator: true), _workId.Value));

        // Assert — the same answer as a work that does not exist: no episode, no title.
        Assert.Null(hidden);
        Assert.NotNull(forOperator);
    }

    [Fact]
    public async Task Progress_carries_the_reported_duration()
    {
        // Arrange
        var userId = Uuid7.New();
        var assetId = await RegisterEpisodeAssetAsync(_episodes[0]);
        await WatchAsync(userId, assetId, positionTicks: 400);

        // Act
        var progress = await QueryAsync(q => q.GetProgressAsync(userId, assetId));

        // Assert — enough to draw the position as a share of the whole.
        Assert.NotNull(progress);
        Assert.Equal(400, progress.PositionTicks);
        Assert.Equal(Duration, progress.DurationTicks);
        Assert.Equal(_workId.Value, progress.WorkId);
        Assert.NotNull(progress.UpdatedAt);
    }

    [Fact]
    public async Task In_progress_lists_started_unfinished_items_most_recent_first()
    {
        // Arrange — E01 watched to the end, E02 then E03 left part-way, and a second viewer's row.
        var viewer = new Viewer(Uuid7.New(), IsAdministrator: false);
        var assetIds = new List<Guid>();
        foreach (var episode in _episodes)
        {
            assetIds.Add(await RegisterEpisodeAssetAsync(episode));
        }

        await WatchAsync(viewer.UserId, assetIds[0], positionTicks: 960);
        await WatchAsync(viewer.UserId, assetIds[1], positionTicks: 400);
        await WatchAsync(viewer.UserId, assetIds[2], positionTicks: 300);
        await WatchAsync(Uuid7.New(), assetIds[0], positionTicks: 500);

        // Act
        var inProgress = await QueryAsync(q => q.GetInProgressAsync(viewer, limit: 10));
        var firstOnly = await QueryAsync(q => q.GetInProgressAsync(viewer, limit: 1));

        // Assert — the watched episode and the other viewer's row are absent; the latest comes first.
        Assert.Equal([assetIds[2], assetIds[1]], inProgress.Select(p => p.AssetId));
        Assert.All(inProgress, p => Assert.Equal(Duration, p.DurationTicks));
        Assert.Equal([assetIds[2]], firstOnly.Select(p => p.AssetId));
    }

    [Fact]
    public async Task In_progress_leaves_out_a_title_the_viewer_may_not_see()
    {
        // Arrange — a started episode, then the series moves to a shelf the member holds no grant for.
        var member = new Viewer(Uuid7.New(), IsAdministrator: false);
        var assetId = await RegisterEpisodeAssetAsync(_episodes[0]);
        await WatchAsync(member.UserId, assetId, positionTicks: 400);
        Assert.Single(await QueryAsync(q => q.GetInProgressAsync(member, limit: 10)));

        await using (var scope = _provider.CreateAsyncScope())
        {
            var collections = scope.ServiceProvider.GetRequiredService<ICollectionAdministration>();
            var shelf = await collections.CreateAsync("Late night", CollectionKind.Series, CollectionAccessMode.Restricted);
            Assert.True((await collections.MoveWorkAsync(_workId, shelf.Value)).IsSuccess);
        }

        // Act
        var inProgress = await QueryAsync(q => q.GetInProgressAsync(member, limit: 10));

        // Assert — the row still exists, but it names a title the member can no longer see.
        Assert.Empty(inProgress);
    }

    [Fact]
    public async Task Next_up_is_null_for_a_movie()
    {
        // Arrange — the movie path: one work, one asset, its own work as its unit.
        var userId = Uuid7.New();
        Guid movieWorkId;
        await using (var scope = _provider.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<ICatalogCommands>()
                .AddMovieAsync("Movie", 2024, [new ExternalId(MetadataProvider.Tmdb, "999")]);
            movieWorkId = result.Value.Value;
        }

        var assetId = Uuid7.New();
        await RegisterAsync(assetId, movieWorkId, "/data/library/Movie (2024)/Movie.2024.mkv", [movieWorkId]);
        await WatchAsync(userId, assetId, positionTicks: 400);

        // Act
        var nextUp = await QueryAsync(q => q.GetNextUpAsync(new Viewer(userId, IsAdministrator: false), movieWorkId));

        // Assert — a movie has no next episode, watched or not.
        Assert.Null(nextUp);
    }

    // -- helpers ---------------------------------------------------------------------------------

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

        await using var scope = _provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ILibraryCommands>().RegisterMediaAssetAsync(request);
    }

    /// <summary>Opens a session on the asset and reports one position — the only way progress is written.</summary>
    private async Task WatchAsync(Guid userId, Guid assetId, long positionTicks)
    {
        var capability = new ClientCapability(["matroska"], ["h264"], ["aac"], MaxHeight: null);

        await using var scope = _provider.CreateAsyncScope();
        var commands = scope.ServiceProvider.GetRequiredService<IPlaybackSessionCommands>();
        // A plain member: the series sits in the default collection, which is open to everyone.
        var result = await commands.RequestPlaybackAsync(new Viewer(userId, IsAdministrator: false), assetId, capability);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
        await commands.ReportProgressAsync(
            new Viewer(userId, IsAdministrator: false), result.Value.SessionId, positionTicks, Duration, isPaused: false);
    }

    private async Task<T> QueryAsync<T>(Func<IPlaybackQuery, Task<T>> query)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<IPlaybackQuery>());
    }
}
