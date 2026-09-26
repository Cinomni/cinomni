using Cinomni.Catalog.Contracts;
using Cinomni.Import.Contracts;
using Cinomni.Monitoring.Contracts;
using Cinomni.Monitoring.Persistence;
using Cinomni.Operations.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Monitoring.Tests;

/// <summary>
/// Nothing in the repository used to clear <c>MonitoredTarget.IsMissing</c>: no <c>WorkAvailable</c> and
/// no <c>MediaAvailable</c> consumer existed. That is a live defect on the movie path — every movie was
/// re-searched every six hours forever — and is unmanageable at episode granularity. These facts pin the
/// fix, including the rollup episode → season → series.
/// </summary>
public sealed class TargetSatisfactionTests : IAsyncLifetime
{
    private static readonly DateOnly Aired = new(2010, 5, 1);

    private ServiceProvider _host = null!;
    private MonitoringDriver _driver = null!;

    public async Task InitializeAsync()
    {
        _host = await MonitoringTestHost.CreateAsync("cinomni_test_monitoring_satisfaction");
        _driver = new MonitoringDriver(_host);
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task An_imported_movie_stops_being_missing()
    {
        var workId = await _driver.AddMovieAsync("Heat", 1995);
        await _driver.DrainAsync();

        await _driver.RunEvaluateMissingAsync();
        Assert.Equal(1, await SearchCountAsync());

        // A pre-series row carries no UnitIds at all; the work id is what it always meant.
        await _driver.PublishAsync(new MediaAvailable(
            Guid.NewGuid(), workId.Value, [], Guid.NewGuid(), Guid.NewGuid(), "/library/heat.mkv", 1024, MediaInfo.Empty));
        await _driver.DrainAsync();

        Assert.False(Assert.Single(await _driver.TargetsAsync(workId)).IsMissing);

        // The point of the fix: the next sweep does not ask for it again, forever.
        await _driver.RewindSearchStampsAsync(workId, TimeSpan.FromDays(7));
        await _driver.RunEvaluateMissingAsync();
        Assert.Equal(1, await SearchCountAsync());
    }

    [Fact]
    public async Task An_imported_episode_stops_being_missing()
    {
        var workId = await BuildSeriesAsync();
        var episode = await EpisodeAsync(workId, season: 1, number: 2);

        await _driver.LandAssetAsync(workId, [episode.TargetRef]);

        Assert.False((await EpisodeAsync(workId, 1, 2)).IsMissing);
        Assert.True((await EpisodeAsync(workId, 1, 1)).IsMissing);
        // One episode of three is not enough to complete the season.
        Assert.True((await SeasonAsync(workId, 1)).IsMissing);
    }

    [Fact]
    public async Task A_season_pack_satisfies_every_episode_target()
    {
        var workId = await BuildSeriesAsync();
        var season = await SeasonAsync(workId, 1);

        // The acquisition goal was the season, so the import lands one unit — it must expand to all three.
        await _driver.LandAssetAsync(workId, [season.TargetRef]);

        var targets = await _driver.TargetsAsync(workId);
        Assert.All(
            targets.Where(t => t is { Kind: TargetKind.Episode, SeasonNumber: 1 }),
            episode => Assert.False(episode.IsMissing));
        Assert.False(targets.Single(t => t is { Kind: TargetKind.Season, SeasonNumber: 1 }).IsMissing);
    }

    [Fact]
    public async Task A_season_rolls_up_to_not_missing_when_its_last_episode_lands()
    {
        var workId = await BuildSeriesAsync();

        await _driver.LandAssetAsync(workId, [(await EpisodeAsync(workId, 1, 1)).TargetRef]);
        await _driver.LandAssetAsync(workId, [(await EpisodeAsync(workId, 1, 2)).TargetRef]);
        Assert.True((await SeasonAsync(workId, 1)).IsMissing);

        await _driver.LandAssetAsync(workId, [(await EpisodeAsync(workId, 1, 3)).TargetRef]);

        Assert.False((await SeasonAsync(workId, 1)).IsMissing);
        // Season 2 is still outstanding, so the series root is too.
        Assert.True((await RootAsync(workId)).IsMissing);
    }

    [Fact]
    public async Task A_series_rolls_up_when_its_last_season_lands()
    {
        var workId = await BuildSeriesAsync();

        await _driver.LandAssetAsync(workId, [(await SeasonAsync(workId, 1)).TargetRef]);
        Assert.True((await RootAsync(workId)).IsMissing);

        await _driver.LandAssetAsync(workId, [(await SeasonAsync(workId, 2)).TargetRef]);

        Assert.False((await SeasonAsync(workId, 2)).IsMissing);
        Assert.False((await RootAsync(workId)).IsMissing);
    }

    [Fact]
    public async Task A_redelivered_media_available_is_a_no_op()
    {
        var workId = await BuildSeriesAsync();
        var episode = await EpisodeAsync(workId, 1, 1);
        var landed = new MediaAvailable(
            Guid.NewGuid(), workId.Value, [], Guid.NewGuid(), Guid.NewGuid(),
            "/library/s01e01.mkv", 1024, MediaInfo.Empty, UnitIds: [episode.TargetRef]);

        await _driver.PublishAsync(landed);
        await _driver.DrainAsync();
        // The relay is at-least-once: the very same event arrives twice.
        await _driver.PublishAsync(landed);
        await _driver.DrainAsync();

        Assert.False((await EpisodeAsync(workId, 1, 1)).IsMissing);
        // The command key units-satisfied:{assetId} is spent, so the second delivery enqueued nothing new.
        Assert.Equal(1, await CommandCountAsync("units-satisfied:"));
    }

    [Fact]
    public async Task An_asset_for_an_unmonitored_work_is_ignored_without_failing()
    {
        await _driver.PublishAsync(new MediaAvailable(
            Guid.NewGuid(), Guid.NewGuid(), [], Guid.NewGuid(), Guid.NewGuid(),
            "/library/stranger.mkv", 1024, MediaInfo.Empty));

        await _driver.DrainAsync();

        // No exception, no poisoned command: the handler logs and succeeds.
        Assert.Equal(0, await FailedCommandCountAsync());
    }

    // -- fixture ---------------------------------------------------------------------------------

    private async Task<WorkId> BuildSeriesAsync()
    {
        var workId = await _driver.AddSeriesAsync("Mad Men", 2007);
        await _driver.DrainAsync();
        await _driver.SyncStructureAsync(
            workId,
            Guid.NewGuid(),
            [new SeasonStructureInput(1), new SeasonStructureInput(2)],
            [
                new EpisodeStructureInput(1, 1, "Smoke", AirDate: Aired),
                new EpisodeStructureInput(1, 2, "Ladies Room", AirDate: Aired),
                new EpisodeStructureInput(1, 3, "Marriage of Figaro", AirDate: Aired),
                new EpisodeStructureInput(2, 1, "For Those Who Think Young", AirDate: Aired),
            ]);
        await _driver.DrainAsync();
        return workId;
    }

    private async Task<MonitoredTarget> RootAsync(WorkId workId) =>
        (await _driver.TargetsAsync(workId)).Single(t => t.Kind == TargetKind.Series);

    private async Task<MonitoredTarget> SeasonAsync(WorkId workId, int number) =>
        (await _driver.TargetsAsync(workId)).Single(t => t.Kind == TargetKind.Season && t.SeasonNumber == number);

    private async Task<MonitoredTarget> EpisodeAsync(WorkId workId, int season, int number) =>
        (await _driver.TargetsAsync(workId))
        .Single(t => t.Kind == TargetKind.Episode && t.SeasonNumber == season && t.EpisodeNumber == number);

    private Task<int> SearchCountAsync() =>
        _driver.OutboxCountAsync(MonitoringEventNames.SearchRequested);

    private async Task<int> CommandCountAsync(string keyPrefix)
    {
        await using var scope = _host.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
        return await dbContext.Commands.CountAsync(c => c.IdempotencyKey.StartsWith(keyPrefix));
    }

    private async Task<int> FailedCommandCountAsync()
    {
        await using var scope = _host.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
        return await dbContext.Commands.CountAsync(c => c.State == CommandState.Failed || c.Error != null);
    }
}
