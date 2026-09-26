using Cinomni.Catalog.Contracts;
using Cinomni.Monitoring.Application;
using Cinomni.Monitoring.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Monitoring.Tests;

/// <summary>
/// The sweep serves a bounded number of works per tick, so <em>which</em> works fill those slots is a
/// correctness property, not a tuning detail: a slot handed to a work that turns out to be inside its
/// cooldown produces no search at all, and a library larger than the quota then never drains.
/// <para>
/// Its own database — every <c>TestHost</c> opens with <c>EnsureDeletedAsync</c>, so sharing a name with
/// another suite would drop that suite's database mid-run.
/// </para>
/// </summary>
public sealed class MissingSweepQuotaTests : IAsyncLifetime
{
    /// <summary>Works beyond the per-sweep quota; they are the ones a wasted slot starves.</summary>
    private const int WorksBeyondTheQuota = 5;

    private readonly MonitoringEventSink _sink = new();
    private ServiceProvider _host = null!;
    private MonitoringDriver _driver = null!;

    public async Task InitializeAsync()
    {
        _host = await MonitoringTestHost.CreateAsync(
            "cinomni_test_monitoring_quota", MonitoringEventSink.Register(_sink));
        _driver = new MonitoringDriver(_host);
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Works_inside_their_cooldown_do_not_consume_the_sweep_quota()
    {
        var total = SearchGranularityPolicy.MaxWorksPerSweep + WorksBeyondTheQuota;
        for (var i = 0; i < total; i++)
        {
            await _driver.AddMovieAsync($"Movie {i:D3}", 1990 + i);
        }

        await _driver.DrainAsync();

        await SweepAsync();
        var firstPass = _sink.Searches.Select(s => s.WorkId).ToHashSet();
        Assert.Equal(SearchGranularityPolicy.MaxWorksPerSweep, firstPass.Count);

        // Forty minutes on: past the coarse thirty-minute pre-filter, nowhere near the six-hour movie
        // cooldown. Selecting works on the coarse filter and only then applying the real cooldown hands
        // these same works every slot again and the sweep publishes nothing at all — for ever, because
        // the works behind them are never even loaded.
        await _driver.RewindAllSearchStampsAsync(TimeSpan.FromMinutes(40));
        _sink.Searches.Clear();

        await SweepAsync();

        var secondPass = _sink.Searches.Select(s => s.WorkId).ToHashSet();
        Assert.Equal(WorksBeyondTheQuota, secondPass.Count);
        Assert.Empty(secondPass.Intersect(firstPass));
    }

    [Fact]
    public async Task A_pack_of_a_just_aired_season_is_re_asked_under_the_short_cooldown()
    {
        // A season that premiered long ago but whose latest episode aired an hour ago: the episodes are
        // due again after thirty minutes, so the sweep loads the work — and the pack must be due too.
        // Grading the pack by the season's own premiere leaves the work selected and the sweep silent,
        // which is the same wasted-slot failure as above, one level up.
        var workId = await _driver.AddSeriesAsync("Tonight Live", 2020);
        await _driver.DrainAsync();
        await _driver.SyncStructureAsync(
            workId,
            Guid.NewGuid(),
            [new SeasonStructureInput(1, AirDate: new DateOnly(2020, 1, 6))],
            Enumerable.Range(1, 5)
                .Select(n => new EpisodeStructureInput(
                    1, n, $"Episode {n}", AirDateTime: DateTimeOffset.UtcNow.AddHours(-1)))
                .ToList());
        await _driver.DrainAsync();

        await SweepAsync();
        var first = Assert.Single(_sink.Searches);
        Assert.Equal(TargetKind.Season.ToString(), first.Criterion.ContentKind);

        await _driver.RewindAllSearchStampsAsync(TimeSpan.FromMinutes(40));
        await SweepAsync();

        Assert.Equal(2, _sink.Searches.Count);
        Assert.All(_sink.Searches, s => Assert.Equal(TargetKind.Season.ToString(), s.Criterion.ContentKind));
    }

    private async Task SweepAsync()
    {
        await _driver.RunEvaluateMissingAsync();
        await _driver.DrainAsync();
    }
}
