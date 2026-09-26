using Cinomni.Catalog.Contracts;
using Cinomni.Monitoring.Application;
using Cinomni.Monitoring.Contracts;
using Cinomni.Monitoring.Persistence;
using Cinomni.Operations.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Monitoring.Tests;

/// <summary>
/// The sweep waits after a known air instant when an operator asks it to. Zero is today's behaviour.
/// An upgrade of something already held does not wait, and a title with no air instant cannot.
/// </summary>
public sealed class SearchDelayTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_just_aired_missing_episode_waits_out_the_delay()
    {
        var episode = Episode(missing: true, airsAt: Now.AddMinutes(-30));

        Assert.Empty(SweepPlanner.PlanForWork([episode], Now, quota: 10, TimeSpan.FromHours(2)));
        Assert.Single(SweepPlanner.PlanForWork([episode], Now, quota: 10));
    }

    [Fact]
    public void An_upgrade_of_a_just_aired_episode_does_not_wait()
    {
        var episode = Episode(missing: false, airsAt: Now.AddMinutes(-30), upgradeWanted: true);

        var planned = Assert.Single(SweepPlanner.PlanForWork([episode], Now, quota: 10, TimeSpan.FromHours(2)));

        Assert.Equal(episode.Id, planned.Target.Id);
    }

    [Fact]
    public void A_movie_with_no_air_instant_is_not_delayed()
    {
        var movie = new MonitoredTarget
        {
            Id = Guid.NewGuid(),
            WorkId = Guid.NewGuid(),
            Kind = TargetKind.Movie,
            TargetRef = Guid.NewGuid(),
            Monitored = true,
            Mode = MonitoringMode.All,
            IsMissing = true,
            CreatedAt = Now,
        };

        Assert.Single(SweepPlanner.PlanForWork([movie], Now, quota: 10, TimeSpan.FromHours(2)));
    }

    private static MonitoredTarget Episode(bool missing, DateTimeOffset airsAt, bool upgradeWanted = false) => new()
    {
        Id = Guid.NewGuid(),
        WorkId = Guid.NewGuid(),
        Kind = TargetKind.Episode,
        TargetRef = Guid.NewGuid(),
        Monitored = true,
        Mode = MonitoringMode.All,
        IsMissing = missing,
        UpgradeWanted = upgradeWanted,
        SeasonNumber = 1,
        EpisodeNumber = 1,
        AirDate = airsAt,
        CreatedAt = Now,
    };
}

/// <summary>
/// The SQL that selects works and the planner that then searches them must apply the same delay.
/// A planner-only test would stay green if the sweep still picked the work and then searched it.
/// </summary>
public sealed class SearchDelaySweepTests : IAsyncLifetime
{
    private readonly MonitoringEventSink _sink = new();
    private ServiceProvider _host = null!;
    private MonitoringDriver _driver = null!;

    public async Task InitializeAsync()
    {
        _host = await MonitoringTestHost.CreateAsync("cinomni_test_monitoring_search_delay", services =>
        {
            MonitoringEventSink.Register(_sink)(services);
            services.AddSingleton<ILiveOptions<MonitoringSweepOptions>>(
                new FixedDelay(TimeSpan.FromHours(2)));
        });
        _driver = new MonitoringDriver(_host);
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task The_sweep_skips_a_missing_episode_still_inside_the_delay()
    {
        var workId = await _driver.AddSeriesAsync("The Nightly Show", 2015);
        await _driver.DrainAsync();
        await _driver.SyncStructureAsync(
            workId,
            Guid.NewGuid(),
            [new SeasonStructureInput(1)],
            [
                new EpisodeStructureInput(
                    1, 1, "Monday",
                    AirDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-2)),
                    AirDateTime: DateTimeOffset.UtcNow.AddDays(-2)),
                new EpisodeStructureInput(
                    1, 2, "Tuesday",
                    AirDate: DateOnly.FromDateTime(DateTime.UtcNow),
                    AirDateTime: DateTimeOffset.UtcNow.AddMinutes(-30)),
            ]);
        await _driver.DrainAsync();

        await _driver.RunEvaluateMissingAsync();
        await _driver.DrainAsync();

        var search = Assert.Single(_sink.Searches);
        Assert.Equal(1, search.Criterion.EpisodeNumber);

        var recent = (await _driver.TargetsAsync(workId))
            .Single(t => t.Kind == TargetKind.Episode && t.EpisodeNumber == 2);
        Assert.Null(recent.LastSearchRequestedAt);
    }

    private sealed class FixedDelay(TimeSpan delay) : ILiveOptions<MonitoringSweepOptions>
    {
        public MonitoringSweepOptions Current { get; } = new() { SearchDelay = delay };
    }
}
