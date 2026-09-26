using Cinomni.Catalog.Contracts;
using Cinomni.Monitoring.Contracts;
using Cinomni.Monitoring.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Monitoring.Tests;

/// <summary>
/// What an interactive search asks for. The plan has to be the sweep's own question — same term, same
/// numbering, same external ids — or a manual search would explain a release against a query the
/// automation never makes. It deliberately ignores the cadence: a person asking for one target has
/// already decided the indexer may be asked.
/// </summary>
public sealed class TargetSearchPlannerTests : IAsyncLifetime
{
    private static readonly DateOnly LongAgo = new(2012, 3, 1);

    private ServiceProvider _host = null!;
    private MonitoringDriver _driver = null!;

    public async Task InitializeAsync()
    {
        _host = await MonitoringTestHost.CreateAsync("cinomni_test_monitoring_plans");
        _driver = new MonitoringDriver(_host);
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task A_movie_target_plans_its_own_work_as_the_unit()
    {
        var workId = await _driver.AddMovieAsync("Dune", 2021);
        await _driver.DrainAsync();
        var movie = (await _driver.TargetsAsync(workId)).Single(t => t.Kind == TargetKind.Movie);

        var plan = await ResolveAsync(movie.Id);

        Assert.NotNull(plan);
        Assert.Equal("Dune", plan.Criterion.Term);
        Assert.Equal(TargetKind.Movie.ToString(), plan.Criterion.ContentKind);
        Assert.Equal(workId.Value, Assert.Single(plan.UnitIds));
        Assert.Equal("Movie", plan.Label);
    }

    [Fact]
    public async Task An_episode_target_plans_its_numbering_and_ids()
    {
        var workId = await _driver.AddSeriesAsync(
            "The Wire", 2002, [new ExternalId(MetadataProvider.Tvdb, "79126")]);
        await _driver.DrainAsync();
        await _driver.SyncStructureAsync(
            workId,
            Guid.NewGuid(),
            [new SeasonStructureInput(2)],
            [new EpisodeStructureInput(2, 5, "Undertow", AbsoluteNumber: 18, AirDate: LongAgo)]);
        await _driver.DrainAsync();

        var episode = (await _driver.TargetsAsync(workId)).Single(t => t.Kind == TargetKind.Episode);
        var plan = await ResolveAsync(episode.Id);

        Assert.NotNull(plan);
        // The series title with the numbers alongside it — never "The Wire S02E05" as a term.
        Assert.Equal("The Wire", plan.Criterion.Term);
        Assert.Equal(2, plan.Criterion.SeasonNumber);
        Assert.Equal(5, plan.Criterion.EpisodeNumber);
        Assert.Equal(18, plan.Criterion.AbsoluteNumber);
        Assert.Equal("79126", plan.Criterion.TvdbId);
        Assert.Equal(episode.TargetRef, Assert.Single(plan.UnitIds));
        Assert.Equal("S02E05", plan.Label);
    }

    [Fact]
    public async Task A_season_target_plans_the_episodes_it_is_missing()
    {
        var workId = await BuildSeasonAsync(episodes: 6);
        var targets = await _driver.TargetsAsync(workId);
        var season = targets.Single(t => t.Kind == TargetKind.Season);

        // Two of the six already landed: a pack search is for the four that are absent.
        var landed = targets
            .Where(t => t.Kind == TargetKind.Episode && t.EpisodeNumber <= 2)
            .Select(t => t.TargetRef)
            .ToList();
        await _driver.LandAssetAsync(workId, landed);

        var plan = await ResolveAsync(season.Id);

        Assert.NotNull(plan);
        Assert.Equal(TargetKind.Season.ToString(), plan.Criterion.ContentKind);
        Assert.Equal(1, plan.Criterion.SeasonNumber);
        Assert.Null(plan.Criterion.EpisodeNumber);
        Assert.Equal(4, plan.UnitIds.Count);
        Assert.DoesNotContain(landed[0], plan.UnitIds);
        Assert.Equal("Season 1", plan.Label);
    }

    [Fact]
    public async Task A_complete_season_plans_all_of_its_episodes()
    {
        var workId = await BuildSeasonAsync(episodes: 3);
        var targets = await _driver.TargetsAsync(workId);
        var season = targets.Single(t => t.Kind == TargetKind.Season);
        await _driver.LandAssetAsync(
            workId, targets.Where(t => t.Kind == TargetKind.Episode).Select(t => t.TargetRef).ToList());

        var plan = await ResolveAsync(season.Id);

        // Nothing is missing, so this can only be a search for something better — and a better pack
        // replaces the whole season, not a subset of it.
        Assert.NotNull(plan);
        Assert.Equal(3, plan.UnitIds.Count);
    }

    [Fact]
    public async Task A_series_root_has_no_plan()
    {
        var workId = await BuildSeasonAsync(episodes: 2);
        var root = (await _driver.TargetsAsync(workId)).Single(t => t.Kind == TargetKind.Series);

        // Searching the root would mean "download the whole show" — which is what its seasons are for.
        Assert.Null(await ResolveAsync(root.Id));
    }

    [Fact]
    public async Task An_unknown_target_has_no_plan()
    {
        Assert.Null(await ResolveAsync(Guid.NewGuid()));
    }

    private async Task<TargetSearchPlan?> ResolveAsync(Guid targetId)
    {
        await using var scope = _host.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ITargetSearchPlans>()
            .ResolveAsync(new MonitoredTargetId(targetId));
    }

    private async Task<WorkId> BuildSeasonAsync(int episodes)
    {
        var workId = await _driver.AddSeriesAsync("Justified", 2010);
        await _driver.DrainAsync();
        await _driver.SyncStructureAsync(
            workId,
            Guid.NewGuid(),
            [new SeasonStructureInput(1)],
            Enumerable.Range(1, episodes)
                .Select(n => new EpisodeStructureInput(1, n, $"Episode {n}", AirDate: LongAgo))
                .ToList());
        await _driver.DrainAsync();
        return workId;
    }
}
