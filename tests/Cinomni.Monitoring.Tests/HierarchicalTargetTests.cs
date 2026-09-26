using Cinomni.Catalog.Contracts;
using Cinomni.Kernel.Identifiers;
using Cinomni.Monitoring.Contracts;
using Cinomni.Monitoring.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Monitoring.Tests;

/// <summary>
/// Guards the schema change at the heart of the hierarchy: <c>ux_monitored_targets_work_id</c> is gone
/// (a work now holds many targets) and <c>ux_monitored_targets_work_ref</c> replaced it in the same
/// migration, so the concurrency guard the module converges on was never absent.
/// </summary>
public sealed class HierarchicalTargetTests : IAsyncLifetime
{
    private ServiceProvider _host = null!;
    private MonitoringDriver _driver = null!;

    public async Task InitializeAsync()
    {
        _host = await MonitoringTestHost.CreateAsync("cinomni_test_monitoring_hierarchy");
        _driver = new MonitoringDriver(_host);
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task A_work_may_hold_a_root_and_many_season_and_episode_targets()
    {
        var workId = await _driver.AddSeriesAsync("The Wire", 2002);
        await _driver.DrainAsync();
        await _driver.SyncStructureAsync(
            workId,
            Guid.NewGuid(),
            [new SeasonStructureInput(1), new SeasonStructureInput(2)],
            [
                new EpisodeStructureInput(1, 1, "The Target"),
                new EpisodeStructureInput(1, 2, "The Detail"),
                new EpisodeStructureInput(2, 1, "Ebb Tide"),
            ]);
        await _driver.DrainAsync();

        var targets = await _driver.TargetsAsync(workId);

        // The row the old unique index made impossible: one work, six targets.
        Assert.Equal(6, targets.Count);
        var root = Assert.Single(targets, t => t.Kind == TargetKind.Series);
        Assert.Equal(workId.Value, root.TargetRef);
        Assert.Null(root.ParentTargetId);

        var seasons = targets.Where(t => t.Kind == TargetKind.Season).ToList();
        Assert.Equal(2, seasons.Count);
        Assert.All(seasons, season => Assert.Equal(root.Id, season.ParentTargetId));

        var episodes = targets.Where(t => t.Kind == TargetKind.Episode).ToList();
        Assert.Equal(3, episodes.Count);
        Assert.All(episodes, episode => Assert.Contains(seasons, s => s.Id == episode.ParentTargetId));
        Assert.Contains(episodes, e => e is { SeasonNumber: 1, EpisodeNumber: 2, EpisodeTitle: "The Detail" });
    }

    [Fact]
    public async Task Two_targets_for_the_same_unit_violate_the_composite_index()
    {
        var workId = await _driver.AddMovieAsync("Heat", 1995);
        await _driver.ApplyPolicyAsync(workId, MonitoringMode.All);

        await using var scope = _host.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MonitoringDbContext>();
        dbContext.MonitoredTargets.Add(new MonitoredTarget
        {
            Id = Uuid7.New(),
            WorkId = workId.Value,
            Kind = TargetKind.Movie,
            TargetRef = workId.Value,
            Monitored = true,
            Mode = MonitoringMode.All,
            IsMissing = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => dbContext.SaveChangesAsync());
    }

    [Fact]
    public async Task A_second_unit_of_the_same_work_is_allowed_by_the_composite_index()
    {
        // The other half of the swap: the old ux_monitored_targets_work_id would have rejected this row.
        var workId = await _driver.AddSeriesAsync("Fringe", 2008);
        await _driver.ApplyPolicyAsync(workId, MonitoringMode.All);

        await using var scope = _host.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MonitoringDbContext>();
        dbContext.MonitoredTargets.Add(new MonitoredTarget
        {
            Id = Uuid7.New(),
            WorkId = workId.Value,
            Kind = TargetKind.Season,
            TargetRef = Uuid7.New(),
            Monitored = true,
            Mode = MonitoringMode.All,
            IsMissing = true,
            SeasonNumber = 1,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await dbContext.SaveChangesAsync();
        Assert.Equal(2, (await _driver.TargetsAsync(workId)).Count);
    }

    [Fact]
    public async Task A_concurrent_apply_policy_converges_to_one_root()
    {
        var workId = await _driver.AddMovieAsync("Sicario", 2015);

        // Four independent scopes means four connections racing on the composite unique index; the losers
        // must catch the violation and converge on the winner's row rather than surfacing a failure.
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            await using var scope = _host.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IMonitoringCommands>()
                .ApplyMonitoringPolicyAsync(workId, MonitoringMode.All);
        }));

        Assert.All(results, result => Assert.True(result.IsSuccess));
        Assert.Single(results.Select(r => r.Value).Distinct());
        Assert.Single(await _driver.TargetsAsync(workId));
    }

    [Fact]
    public async Task Get_by_work_returns_the_root_not_an_arbitrary_child()
    {
        var workId = await _driver.AddSeriesAsync("Deadwood", 2004);
        await _driver.DrainAsync();
        await _driver.SyncStructureAsync(
            workId,
            Guid.NewGuid(),
            [new SeasonStructureInput(1)],
            [new EpisodeStructureInput(1, 1), new EpisodeStructureInput(1, 2)]);
        await _driver.DrainAsync();

        await using var scope = _host.CreateAsyncScope();
        var target = await scope.ServiceProvider.GetRequiredService<IMonitoringQuery>().GetByWorkAsync(workId);

        Assert.NotNull(target);
        Assert.Equal(TargetKind.Series, target!.Kind);
        Assert.Equal(workId.Value, target.TargetRef);
        Assert.Null(target.ParentId);
        // The root's counters are the whole work's, so one object still answers the detail page.
        Assert.Equal(2, target.TotalCount);
        Assert.Equal(2, target.MissingCount);
    }
}
