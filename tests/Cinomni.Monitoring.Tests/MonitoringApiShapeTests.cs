using Cinomni.Catalog.Contracts;
using Cinomni.Monitoring.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Monitoring.Tests;

/// <summary>
/// Guards the read shapes the API projects. Exercised at the service level rather than over HTTP: the
/// endpoints are a thin projection of these results, and the thing that must not regress is the shape —
/// <c>GET /works/{id}/target</c> returns exactly one object because the web client reads it directly.
/// </summary>
public sealed class MonitoringApiShapeTests : IAsyncLifetime
{
    private static readonly DateOnly Aired = new(2011, 4, 17);

    private readonly MonitoringEventSink _sink = new();
    private ServiceProvider _host = null!;
    private MonitoringDriver _driver = null!;

    public async Task InitializeAsync()
    {
        _host = await MonitoringTestHost.CreateAsync(
            "cinomni_test_monitoring_api", MonitoringEventSink.Register(_sink));
        _driver = new MonitoringDriver(_host);
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Target_for_work_still_returns_a_single_root_for_a_movie()
    {
        var workId = await _driver.AddMovieAsync("Heat", 1995);
        await _driver.DrainAsync();

        var target = await QueryAsync(query => query.GetByWorkAsync(workId));

        Assert.NotNull(target);
        Assert.Equal(TargetKind.Movie, target!.Kind);
        Assert.Equal(workId.Value, target.TargetRef);
        Assert.Null(target.ParentId);
        Assert.Null(target.SeasonNumber);
        // A movie has no episodes to count, so the counters stay 0 and no consumer has to branch on kind.
        Assert.Equal(0, target.TotalCount);
    }

    [Fact]
    public async Task Target_for_work_returns_the_series_root_not_a_season()
    {
        var workId = await BuildSeriesAsync();

        var target = await QueryAsync(query => query.GetByWorkAsync(workId));

        Assert.NotNull(target);
        Assert.Equal(TargetKind.Series, target!.Kind);
        Assert.Null(target.SeasonNumber);
        Assert.Equal(workId.Value, target.TargetRef);
    }

    [Fact]
    public async Task Targets_for_work_returns_the_full_tree_with_counters()
    {
        var workId = await BuildSeriesAsync();

        var tree = await QueryAsync(query => query.ListByWorkAsync(workId));

        // Root first, then season 1 with its episodes, then season 2 with its episode.
        Assert.Equal(
            new[]
            {
                TargetKind.Series, TargetKind.Season, TargetKind.Episode, TargetKind.Episode,
                TargetKind.Season, TargetKind.Episode,
            },
            tree.Select(t => t.Kind).ToArray());

        var root = tree[0];
        Assert.Equal(3, root.TotalCount);
        Assert.Equal(3, root.MissingCount);

        var seasonOne = tree.Single(t => t.Kind == TargetKind.Season && t.SeasonNumber == 1);
        Assert.Equal(2, seasonOne.TotalCount);
        Assert.Equal(2, seasonOne.MissingCount);
        Assert.Equal(root.Id, seasonOne.ParentId);

        var episode = tree.Single(t => t is { Kind: TargetKind.Episode, SeasonNumber: 1, EpisodeNumber: 2 });
        Assert.Equal(seasonOne.Id, episode.ParentId);
        Assert.Equal("Winter Is Coming", tree.Single(t => t is { SeasonNumber: 1, EpisodeNumber: 1 }).Title);
        Assert.NotNull(episode.AirDate);

        // One landed episode moves the counters, so the detail page never has to count client-side.
        await _driver.LandAssetAsync(workId, [episode.TargetRef]);
        var refreshed = await QueryAsync(query => query.ListByWorkAsync(workId));
        Assert.Equal(2, refreshed[0].MissingCount);
    }

    [Fact]
    public async Task Listing_seasons_returns_only_the_season_rows_in_order()
    {
        var workId = await BuildSeriesAsync();

        var seasons = await QueryAsync(query => query.ListSeasonsAsync(workId));

        Assert.Equal([1, 2], seasons.Select(s => s.SeasonNumber!.Value).ToArray());
        Assert.All(seasons, season => Assert.Equal(TargetKind.Season, season.Kind));
    }

    [Fact]
    public async Task Listing_targets_is_bounded_by_the_page_size()
    {
        var workId = await BuildSeriesAsync();

        var firstPage = await QueryAsync(query => query.ListAsync(limit: 2));
        Assert.Equal(2, firstPage.Count);

        var secondPage = await QueryAsync(query => query.ListAsync(limit: 2, offset: 2));
        Assert.Equal(2, secondPage.Count);
        Assert.Empty(firstPage.Select(t => t.Id).Intersect(secondPage.Select(t => t.Id)));

        // An absurd page size is clamped rather than honoured.
        Assert.True((await QueryAsync(query => query.ListAsync(limit: 100_000))).Count <= MonitoringPaging.MaxPageSize);

        // The missing list is bounded and scopeable to one work.
        var missing = await QueryAsync(query => query.ListMissingAsync(limit: 3, workId: workId));
        Assert.Equal(3, missing.Count);
        Assert.All(missing, target => Assert.Equal(workId, target.WorkId));
    }

    [Fact]
    public async Task Resolving_targets_for_units_maps_catalog_ids_back_to_targets()
    {
        var workId = await BuildSeriesAsync();
        var tree = await QueryAsync(query => query.ListByWorkAsync(workId));
        var episode = tree.Single(t => t is { Kind: TargetKind.Episode, SeasonNumber: 2 });

        var resolved = await QueryAsync(query => query.ResolveTargetsForUnitsAsync([episode.TargetRef]));

        Assert.Equal(episode.Id, Assert.Single(resolved).Id);
        Assert.Empty(await QueryAsync(query => query.ResolveTargetsForUnitsAsync([])));
    }

    [Fact]
    public async Task Subtree_monitored_cascades_and_publishes_one_event_per_newly_enabled_leaf()
    {
        var workId = await BuildSeriesAsync();
        await _driver.ApplyPolicyAsync(workId, MonitoringMode.None);
        await _driver.DrainAsync();
        _sink.Enabled.Clear();

        var root = (await QueryAsync(query => query.ListByWorkAsync(workId)))
            .Single(t => t.Kind == TargetKind.Series);

        await using (var scope = _host.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<IMonitoringCommands>()
                .SetSubtreeMonitoredAsync(root.Id, monitored: true);
            Assert.True(result.IsSuccess);
        }

        await _driver.DrainAsync();

        // Two seasons and three episodes became acquirable; the Series root did not, because it is not.
        Assert.Equal(5, _sink.Enabled.Count);
        Assert.DoesNotContain(_sink.Enabled, e => e.Kind == TargetKind.Series.ToString());
        Assert.Equal(5, _sink.Enabled.Select(e => e.TargetId).Distinct().Count());
    }

    // -- fixture ---------------------------------------------------------------------------------

    private async Task<WorkId> BuildSeriesAsync()
    {
        var workId = await _driver.AddSeriesAsync("Game of Thrones", 2011);
        await _driver.DrainAsync();
        await _driver.SyncStructureAsync(
            workId,
            Guid.NewGuid(),
            [new SeasonStructureInput(1), new SeasonStructureInput(2)],
            [
                new EpisodeStructureInput(1, 1, "Winter Is Coming", AirDate: Aired),
                new EpisodeStructureInput(1, 2, "The Kingsroad", AirDate: Aired),
                new EpisodeStructureInput(2, 1, "The North Remembers", AirDate: Aired),
            ]);
        await _driver.DrainAsync();
        return workId;
    }

    private async Task<T> QueryAsync<T>(Func<IMonitoringQuery, Task<T>> read)
    {
        await using var scope = _host.CreateAsyncScope();
        return await read(scope.ServiceProvider.GetRequiredService<IMonitoringQuery>());
    }
}
