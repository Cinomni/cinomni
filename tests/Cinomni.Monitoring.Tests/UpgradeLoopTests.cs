using Cinomni.Decision.Contracts;
using Cinomni.Kernel.Identifiers;
using Cinomni.Monitoring.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Monitoring.Tests;

/// <summary>
/// The half of the upgrade loop that lives here: Decision judges what an import landed, and Monitoring
/// records the verdict on the targets so the sweep can act on it without asking anyone.
/// <para>
/// The transition that matters most is the second one. A title marked "look for better" and never
/// unmarked is a title searched every week for the rest of the installation's life, and the moment it
/// has to be unmarked is exactly the moment an upgrade finally succeeded.
/// </para>
/// </summary>
public sealed class UpgradeLoopTests : IAsyncLifetime
{
    private ServiceProvider _host = null!;
    private MonitoringDriver _driver = null!;

    public async Task InitializeAsync()
    {
        _host = await MonitoringTestHost.CreateAsync("cinomni_test_monitoring_upgrade");
        _driver = new MonitoringDriver(_host);
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task A_title_below_the_cutoff_is_marked_for_a_better_release()
    {
        var workId = await _driver.AddMovieAsync("The Matrix", 1999);
        await _driver.ApplyPolicyAsync(workId, MonitoringMode.All);
        await _driver.LandAssetAsync(workId, [workId.Value]);

        await _driver.PublishAsync(new UpgradeAssessed(
            Uuid7.New(), workId.Value, UnitsWantingUpgrade: [workId.Value], UnitsSatisfied: []));
        await _driver.DrainAsync();

        var target = Assert.Single(await _driver.TargetsAsync(workId));
        Assert.True(target.UpgradeWanted);
        Assert.False(target.IsMissing);
    }

    [Fact]
    public async Task Reaching_the_cutoff_stops_the_searching()
    {
        var workId = await _driver.AddMovieAsync("Heat", 1995);
        await _driver.ApplyPolicyAsync(workId, MonitoringMode.All);
        await _driver.LandAssetAsync(workId, [workId.Value]);

        await _driver.PublishAsync(new UpgradeAssessed(
            Uuid7.New(), workId.Value, [workId.Value], []));
        await _driver.DrainAsync();
        Assert.True(Assert.Single(await _driver.TargetsAsync(workId)).UpgradeWanted);

        // The better release landed and was assessed again — this time it is good enough.
        await _driver.PublishAsync(new UpgradeAssessed(
            Uuid7.New(), workId.Value, UnitsWantingUpgrade: [], UnitsSatisfied: [workId.Value]));
        await _driver.DrainAsync();

        Assert.False(Assert.Single(await _driver.TargetsAsync(workId)).UpgradeWanted);
    }

    [Fact]
    public async Task A_marked_title_is_swept_only_after_its_own_long_cooldown()
    {
        var workId = await _driver.AddMovieAsync("Collateral", 2004);
        await _driver.ApplyPolicyAsync(workId, MonitoringMode.All);
        await _driver.LandAssetAsync(workId, [workId.Value]);
        await _driver.PublishAsync(new UpgradeAssessed(Uuid7.New(), workId.Value, [workId.Value], []));
        await _driver.DrainAsync();

        // Six hours would be enough for something missing. It is not enough for this.
        await _driver.RewindAllSearchStampsAsync(TimeSpan.FromHours(12));
        await _driver.RunEvaluateMissingAsync();
        var baseline = await _driver.OutboxCountAsync(MonitoringEventNames.SearchRequested);

        await _driver.RewindAllSearchStampsAsync(TimeSpan.FromDays(8));
        await _driver.RunEvaluateMissingAsync();
        Assert.Equal(baseline + 1, await _driver.OutboxCountAsync(MonitoringEventNames.SearchRequested));

        // ...and having just asked, it goes quiet again.
        await _driver.RunEvaluateMissingAsync();
        Assert.Equal(baseline + 1, await _driver.OutboxCountAsync(MonitoringEventNames.SearchRequested));
    }

    [Fact]
    public async Task An_unmarked_present_title_is_never_swept()
    {
        // The state every target in an existing library starts in.
        var workId = await _driver.AddMovieAsync("Ronin", 1998);
        await _driver.ApplyPolicyAsync(workId, MonitoringMode.All);
        await _driver.LandAssetAsync(workId, [workId.Value]);

        await _driver.RewindAllSearchStampsAsync(TimeSpan.FromDays(30));
        await _driver.RunEvaluateMissingAsync();

        Assert.Equal(0, await _driver.OutboxCountAsync(MonitoringEventNames.SearchRequested));
    }
}
