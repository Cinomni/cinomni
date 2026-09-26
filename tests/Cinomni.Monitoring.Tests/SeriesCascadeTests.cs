using Cinomni.Catalog.Contracts;
using Cinomni.Monitoring.Contracts;
using Cinomni.Monitoring.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Monitoring.Tests;

/// <summary>
/// One fact per <see cref="MonitoringMode"/>, plus the regressions the hierarchy makes possible: a later
/// snapshot must still materialise (the idempotency-key trap), a toggle must not destroy the policy, and
/// the series root must never look acquirable.
/// <para>
/// Every series here has the same shape: a specials season (0), a finished season 1 with three aired
/// episodes, and a running season 2 with one aired episode and one that has not aired yet.
/// </para>
/// </summary>
public sealed class SeriesCascadeTests : IAsyncLifetime
{
    private static readonly DateOnly LongAgo = new(2003, 6, 1);
    private static readonly DateOnly Recently = new(2024, 1, 15);

    private readonly MonitoringEventSink _sink = new();
    private ServiceProvider _host = null!;
    private MonitoringDriver _driver = null!;

    public async Task InitializeAsync()
    {
        _host = await MonitoringTestHost.CreateAsync(
            "cinomni_test_monitoring_cascade", MonitoringEventSink.Register(_sink));
        _driver = new MonitoringDriver(_host);
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task All_monitors_every_episode()
    {
        var workId = await BuildSeriesAsync(MonitoringMode.All);

        Assert.Equal(
            [(0, 1), (1, 1), (1, 2), (1, 3), (2, 1), (2, 2)],
            await MonitoredEpisodesAsync(workId));
    }

    [Fact]
    public async Task None_monitors_nothing()
    {
        var workId = await BuildSeriesAsync(MonitoringMode.None);

        Assert.Empty(await MonitoredEpisodesAsync(workId));
    }

    [Fact]
    public async Task Future_monitors_only_unaired_episodes()
    {
        var workId = await BuildSeriesAsync(MonitoringMode.Future);

        Assert.Equal([(2, 2)], await MonitoredEpisodesAsync(workId));
    }

    [Fact]
    public async Task Existing_monitors_only_aired_episodes()
    {
        var workId = await BuildSeriesAsync(MonitoringMode.Existing);

        Assert.Equal(
            [(0, 1), (1, 1), (1, 2), (1, 3), (2, 1)],
            await MonitoredEpisodesAsync(workId));
    }

    [Fact]
    public async Task Pilot_monitors_only_s01e01()
    {
        var workId = await BuildSeriesAsync(MonitoringMode.Pilot);

        // Season 0 holds specials, so the pilot is S01E01 and not S00E01.
        Assert.Equal([(1, 1)], await MonitoredEpisodesAsync(workId));
    }

    [Fact]
    public async Task FirstSeason_monitors_the_lowest_season()
    {
        var workId = await BuildSeriesAsync(MonitoringMode.FirstSeason);

        Assert.Equal([(1, 1), (1, 2), (1, 3)], await MonitoredEpisodesAsync(workId));
    }

    [Fact]
    public async Task LastSeason_monitors_the_highest_season()
    {
        var workId = await BuildSeriesAsync(MonitoringMode.LastSeason);

        Assert.Equal([(2, 1), (2, 2)], await MonitoredEpisodesAsync(workId));
    }

    [Fact]
    public async Task A_new_season_from_a_later_snapshot_is_materialised()
    {
        // The regression that proves the sync is NOT keyed on apply-policy:{workId}: that key is consumed
        // forever by WorkAddedHandler, so a work-keyed command would silently drop every later season.
        var workId = await BuildSeriesAsync(MonitoringMode.All);

        await _driver.SyncStructureAsync(
            workId,
            Guid.NewGuid(),
            [new SeasonStructureInput(3)],
            [
                new EpisodeStructureInput(3, 1, "Fresh", AirDate: Recently),
                new EpisodeStructureInput(3, 2, "Air", AirDate: Recently),
            ]);
        await _driver.DrainAsync();

        var monitored = await MonitoredEpisodesAsync(workId);
        Assert.Contains((3, 1), monitored);
        Assert.Contains((3, 2), monitored);

        var targets = await _driver.TargetsAsync(workId);
        Assert.Equal(3 + 1, targets.Count(t => t.Kind == TargetKind.Season)); // 0, 1, 2 and the new 3
    }

    [Fact]
    public async Task A_resync_does_not_undo_a_manual_toggle()
    {
        var workId = await BuildSeriesAsync(MonitoringMode.All);
        var episode = (await _driver.TargetsAsync(workId))
            .Single(t => t is { Kind: TargetKind.Episode, SeasonNumber: 1, EpisodeNumber: 2 });

        await using (var scope = _host.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IMonitoringCommands>()
                .SetTargetMonitoredAsync(new MonitoredTargetId(episode.Id), monitored: false);
        }

        // A later snapshot re-syncs the very same structure; the user's "off" must survive it.
        await _driver.SyncStructureAsync(workId, Guid.NewGuid(), Seasons(), Episodes());
        await _driver.DrainAsync();

        Assert.DoesNotContain((1, 2), await MonitoredEpisodesAsync(workId));
    }

    [Fact]
    public async Task Toggling_a_target_does_not_destroy_its_policy()
    {
        var workId = await BuildSeriesAsync(MonitoringMode.FirstSeason);
        var root = (await _driver.TargetsAsync(workId)).Single(t => t.Kind == TargetKind.Series);
        Assert.Equal(MonitoringMode.FirstSeason, root.Mode);

        await using (var scope = _host.CreateAsyncScope())
        {
            var commands = scope.ServiceProvider.GetRequiredService<IMonitoringCommands>();
            await commands.SetTargetMonitoredAsync(new MonitoredTargetId(root.Id), monitored: false);
            await commands.SetTargetMonitoredAsync(new MonitoredTargetId(root.Id), monitored: true);
        }

        var after = (await _driver.TargetsAsync(workId)).Single(t => t.Kind == TargetKind.Series);
        Assert.True(after.Monitored);
        // One UI click used to write All/None here and wipe the user's policy.
        Assert.Equal(MonitoringMode.FirstSeason, after.Mode);
    }

    [Fact]
    public async Task Enabling_the_subtree_cascades_to_children()
    {
        var workId = await BuildSeriesAsync(MonitoringMode.None);
        Assert.Empty(await MonitoredEpisodesAsync(workId));

        var root = (await _driver.TargetsAsync(workId)).Single(t => t.Kind == TargetKind.Series);
        await using (var scope = _host.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<IMonitoringCommands>()
                .SetSubtreeMonitoredAsync(new MonitoredTargetId(root.Id), monitored: true);
            Assert.True(result.IsSuccess);
        }

        Assert.Equal(6, (await MonitoredEpisodesAsync(workId)).Count);
        var targets = await _driver.TargetsAsync(workId);
        Assert.All(targets, target => Assert.True(target.Monitored));
    }

    [Fact]
    public async Task Disabling_a_season_subtree_leaves_the_other_seasons_alone()
    {
        var workId = await BuildSeriesAsync(MonitoringMode.All);
        var season1 = (await _driver.TargetsAsync(workId))
            .Single(t => t is { Kind: TargetKind.Season, SeasonNumber: 1 });

        await using (var scope = _host.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IMonitoringCommands>()
                .SetSubtreeMonitoredAsync(new MonitoredTargetId(season1.Id), monitored: false);
        }

        var monitored = await MonitoredEpisodesAsync(workId);
        Assert.DoesNotContain((1, 1), monitored);
        Assert.DoesNotContain((1, 3), monitored);
        Assert.Contains((2, 1), monitored);
        Assert.Contains((0, 1), monitored);
    }

    [Fact]
    public async Task The_series_root_publishes_no_acquirable_monitoring_enabled()
    {
        var workId = await BuildSeriesAsync(MonitoringMode.All);
        await _driver.DrainAsync();

        var root = (await _driver.TargetsAsync(workId)).Single(t => t.Kind == TargetKind.Series);

        // The root is a policy node: nothing announces it as something to acquire, so Acquisition never
        // opens an intent meaning "download the whole show".
        Assert.DoesNotContain(_sink.Enabled, e => e.TargetId == root.Id);
        Assert.DoesNotContain(_sink.Enabled, e => e.Kind == TargetKind.Series.ToString());

        // Its acquirable descendants do announce themselves, carrying the catalog unit they watch.
        Assert.Equal(6, _sink.Enabled.Count(e => e.Kind == TargetKind.Episode.ToString()));
        Assert.Equal(3, _sink.Enabled.Count(e => e.Kind == TargetKind.Season.ToString()));
        Assert.All(_sink.Enabled, e => Assert.NotEqual(Guid.Empty, e.UnitId ?? Guid.Empty));
    }

    [Fact]
    public async Task A_movie_still_gets_exactly_one_target_and_one_enable()
    {
        var workId = await _driver.AddMovieAsync("Heat", 1995);
        await _driver.DrainAsync();

        var target = Assert.Single(await _driver.TargetsAsync(workId));
        Assert.Equal(TargetKind.Movie, target.Kind);
        var enabled = Assert.Single(_sink.Enabled, e => e.WorkId == workId.Value);
        Assert.Equal(TargetKind.Movie.ToString(), enabled.Kind);
        Assert.Equal(workId.Value, enabled.UnitId);
    }

    // -- fixture ---------------------------------------------------------------------------------

    /// <summary>Catalogues the series, gives it its structure, then applies <paramref name="mode"/>.</summary>
    private async Task<WorkId> BuildSeriesAsync(MonitoringMode mode)
    {
        var workId = await _driver.AddSeriesAsync("Battlestar Galactica", 2003);
        await _driver.DrainAsync();
        await _driver.SyncStructureAsync(workId, Guid.NewGuid(), Seasons(), Episodes());
        await _driver.DrainAsync();

        await _driver.ApplyPolicyAsync(workId, mode);
        await _driver.DrainAsync();
        return workId;
    }

    private static IReadOnlyList<SeasonStructureInput> Seasons() =>
    [
        new SeasonStructureInput(0, "Specials"),
        new SeasonStructureInput(1, "Season 1"),
        new SeasonStructureInput(2, "Season 2"),
    ];

    private static IReadOnlyList<EpisodeStructureInput> Episodes() =>
    [
        new EpisodeStructureInput(0, 1, "The Mini-Series", AirDate: LongAgo),
        new EpisodeStructureInput(1, 1, "33", AbsoluteNumber: 1, AirDate: LongAgo),
        new EpisodeStructureInput(1, 2, "Water", AbsoluteNumber: 2, AirDate: LongAgo),
        new EpisodeStructureInput(1, 3, "Bastille Day", AbsoluteNumber: 3, AirDate: LongAgo),
        new EpisodeStructureInput(2, 1, "Scattered", AbsoluteNumber: 4, AirDate: Recently),
        // The only unaired episode: dated well beyond any plausible test run.
        new EpisodeStructureInput(2, 2, "Valley of Darkness", AbsoluteNumber: 5, AirDate: new DateOnly(2999, 1, 1)),
    ];

    private async Task<List<(int Season, int Episode)>> MonitoredEpisodesAsync(WorkId workId)
    {
        var targets = await _driver.TargetsAsync(workId);
        return targets
            .Where(t => t is { Kind: TargetKind.Episode, Monitored: true })
            .OrderBy(t => t.SeasonNumber)
            .ThenBy(t => t.EpisodeNumber)
            .Select(t => (t.SeasonNumber!.Value, t.EpisodeNumber!.Value))
            .ToList();
    }
}
