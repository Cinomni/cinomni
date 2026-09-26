using Cinomni.Catalog.Contracts;
using Cinomni.Catalog.Persistence;
using Cinomni.Kernel.Identifiers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Catalog.Tests;

/// <summary>
/// The release→unit resolver contract, against a real PostgreSQL instance. This is the suite every
/// downstream module leans on: Monitoring materialises targets from it, Decision computes pack coverage
/// with it and Import maps a landed file to an episode with it. Getting a resolver wrong does not throw —
/// it silently links an asset to the wrong episode, and <c>asset_unit_links</c> then persists that.
/// <para>
/// The fixture is one show shaped to exercise every resolution mode at once: a specials season, two
/// numbered seasons, anime-style absolute numbers, and a date on which two episodes aired.
/// </para>
/// </summary>
public sealed class EpisodeResolutionTests : IAsyncLifetime
{
    private const string Database = "cinomni_test_catalog_resolver";

    private static readonly DateOnly DoubleBillDate = new(2026, 7, 28);

    private SqlCapture _sql = null!;
    private ServiceProvider _provider = null!;
    private WorkId _workId;

    public async Task InitializeAsync()
    {
        _provider = await CatalogTestHost.CreateAsync(Database, services => _sql = SqlCapture.Register(services));
        _workId = await SeedAsync();
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task Resolves_sxxeyy_hit_and_miss()
    {
        var hit = await ResolveAsync(q => q.ResolveEpisodeAsync(_workId, 2, 5));
        var episode = Assert.Single(hit);
        Assert.Equal(2, episode.SeasonNumber);
        Assert.Equal(5, episode.Number);
        Assert.Equal("S02E05", episode.Title);

        // A season that exists but an episode that does not, and a season that does not exist at all.
        Assert.Empty(await ResolveAsync(q => q.ResolveEpisodeAsync(_workId, 2, 99)));
        Assert.Empty(await ResolveAsync(q => q.ResolveEpisodeAsync(_workId, 9, 1)));
    }

    [Fact]
    public async Task Resolves_a_multi_episode_range_in_order()
    {
        var episodes = await ResolveAsync(q => q.ResolveEpisodeRangeAsync(_workId, 1, 2, 4));

        // Inclusive at both ends, ordered — a multi-episode file links its units in broadcast order.
        Assert.Equal(new[] { 2, 3, 4 }, episodes.Select(e => e.Number));
        Assert.All(episodes, e => Assert.Equal(1, e.SeasonNumber));
    }

    [Fact]
    public async Task Resolves_a_season_pack_to_every_episode_of_the_season()
    {
        var episodes = await ResolveAsync(q => q.ResolveSeasonAsync(_workId, 1));

        // A pack satisfies N units at once; that is why every resolver returns a list.
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, episodes.Select(e => e.Number));
        Assert.Empty(await ResolveAsync(q => q.ResolveSeasonAsync(_workId, 42)));
    }

    [Fact]
    public async Task Resolves_an_absolute_number_to_the_right_sxxeyy()
    {
        // Absolute 7 is the second episode of season two: the whole point of anime numbering is that it
        // does not agree with SxxEyy.
        var episode = Assert.Single(await ResolveAsync(q => q.ResolveByAbsoluteNumberAsync(_workId, 7)));
        Assert.Equal(2, episode.SeasonNumber);
        Assert.Equal(2, episode.Number);
        Assert.Equal(7, episode.AbsoluteNumber);

        Assert.Empty(await ResolveAsync(q => q.ResolveByAbsoluteNumberAsync(_workId, 999)));
    }

    [Fact]
    public async Task Resolves_a_date_based_episode_and_returns_both_when_two_share_an_air_date()
    {
        var same = await ResolveAsync(q => q.ResolveByAirDateAsync(_workId, DoubleBillDate));

        // A daily show can air two episodes on one date; collapsing that to one would drop a unit.
        Assert.Equal(2, same.Count);
        Assert.Equal(new[] { 4, 5 }, same.Select(e => e.Number));
        Assert.All(same, e => Assert.Equal(1, e.SeasonNumber));

        var single = Assert.Single(await ResolveAsync(q => q.ResolveByAirDateAsync(_workId, new DateOnly(2026, 7, 20))));
        Assert.Equal(1, single.Number);

        Assert.Empty(await ResolveAsync(q => q.ResolveByAirDateAsync(_workId, new DateOnly(1999, 1, 1))));
    }

    [Fact]
    public async Task Resolves_season_zero_specials()
    {
        // Season 0 is a real season, not a sentinel: S00E01 must resolve like any other episode.
        var special = Assert.Single(await ResolveAsync(q => q.ResolveEpisodeAsync(_workId, 0, 1)));
        Assert.Equal(0, special.SeasonNumber);
        Assert.Equal("S00E01", special.Title);

        // ...and it must sort first when the seasons are listed.
        var seasons = await SeasonsAsync(_workId);
        Assert.Equal(new[] { 0, 1, 2 }, seasons.Select(s => s.Number));
    }

    [Fact]
    public async Task Returns_empty_for_an_unknown_work()
    {
        var unknown = new WorkId(Uuid7.New());

        // Every resolver is a miss, never an exception: resolution runs against hostile release titles.
        Assert.Empty(await ResolveAsync(q => q.ResolveEpisodeAsync(unknown, 1, 1)));
        Assert.Empty(await ResolveAsync(q => q.ResolveEpisodeRangeAsync(unknown, 1, 1, 3)));
        Assert.Empty(await ResolveAsync(q => q.ResolveSeasonAsync(unknown, 1)));
        Assert.Empty(await ResolveAsync(q => q.ResolveByAbsoluteNumberAsync(unknown, 1)));
        Assert.Empty(await ResolveAsync(q => q.ResolveByAirDateAsync(unknown, DoubleBillDate)));
        Assert.Empty(await SeasonsAsync(unknown));
        Assert.Empty(await ResolveAsync(q => q.GetEpisodesAsync(unknown, 1)));
    }

    [Fact]
    public async Task Get_episodes_returns_one_season_in_order()
    {
        var episodes = await ResolveAsync(q => q.GetEpisodesAsync(_workId, 2));

        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, episodes.Select(e => e.Number));
        Assert.All(episodes, e => Assert.Equal(_workId, e.WorkId));
    }

    [Fact]
    public async Task Listing_works_does_not_load_episodes()
    {
        await using var scope = _provider.CreateAsyncScope();
        var query = scope.ServiceProvider.GetRequiredService<ICatalogQuery>();

        _sql.Clear();
        var works = await query.ListAsync();

        // The library grid renders this on every load. An Include of the hierarchy would be an immediate
        // cartesian product, so the read must never mention either child table.
        Assert.NotEmpty(_sql.Statements);
        Assert.False(_sql.Touched("episodes"), string.Join("\n", _sql.Statements));
        Assert.False(_sql.Touched("seasons"), string.Join("\n", _sql.Statements));

        // The counters still arrive — they are denormalised onto the work for exactly this reason.
        var work = Assert.Single(works);
        Assert.Equal(11, work.EpisodeCount);
        Assert.Equal(1, work.AvailableEpisodeCount);
    }

    // -- helpers ---------------------------------------------------------------------------------

    private async Task<IReadOnlyList<EpisodeSummary>> ResolveAsync(
        Func<ICatalogSeriesQuery, Task<IReadOnlyList<EpisodeSummary>>> resolve)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await resolve(scope.ServiceProvider.GetRequiredService<ICatalogSeriesQuery>());
    }

    private async Task<IReadOnlyList<SeasonSummary>> SeasonsAsync(WorkId workId)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ICatalogSeriesQuery>().GetSeasonsAsync(workId);
    }

    /// <summary>
    /// Seeds one show directly through the DbContext: this suite pins the read contract, so the fixture
    /// stays independent of the write path that produces it.
    /// </summary>
    private async Task<WorkId> SeedAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();

        var work = new Work
        {
            Id = Uuid7.New(),
            Kind = WorkKind.Series,
            Title = "Resolver Show",
            SortTitle = "resolver show",
            Status = WorkStatus.Continuing,
            AddedAt = DateTimeOffset.UtcNow,
            EpisodeCount = 11,
            AvailableEpisodeCount = 1,
        };
        dbContext.Works.Add(work);

        AddSeason(dbContext, work.Id, 0, [(1, null, null)]);
        AddSeason(dbContext, work.Id, 1,
        [
            (1, 1, new DateOnly(2026, 7, 20)),
            (2, 2, new DateOnly(2026, 7, 21)),
            (3, 3, new DateOnly(2026, 7, 22)),
            (4, 4, DoubleBillDate),
            (5, 5, DoubleBillDate),
        ]);
        AddSeason(dbContext, work.Id, 2,
        [
            (1, 6, new DateOnly(2026, 8, 1)),
            (2, 7, new DateOnly(2026, 8, 8)),
            (3, 8, new DateOnly(2026, 8, 15)),
            (4, 9, new DateOnly(2026, 8, 22)),
            (5, 10, new DateOnly(2026, 8, 29)),
        ]);

        await dbContext.SaveChangesAsync();

        // One episode carries an asset, so the rollup assertion is not trivially zero.
        var first = await dbContext.Episodes.SingleAsync(e => e.WorkId == work.Id && e.SeasonNumber == 1 && e.Number == 1);
        first.MarkAvailable();
        await dbContext.SaveChangesAsync();

        return new WorkId(work.Id);
    }

    private static void AddSeason(
        CatalogDbContext dbContext,
        Guid workId,
        int seasonNumber,
        IReadOnlyList<(int Number, int? Absolute, DateOnly? AirDate)> episodes)
    {
        var now = DateTimeOffset.UtcNow;
        var season = new Season { Id = Uuid7.New(), WorkId = workId, Number = seasonNumber, AddedAt = now };
        dbContext.Seasons.Add(season);

        foreach (var (number, absolute, airDate) in episodes)
        {
            dbContext.Episodes.Add(new Episode
            {
                Id = Uuid7.New(),
                SeasonId = season.Id,
                WorkId = workId,
                SeasonNumber = seasonNumber,
                Number = number,
                AbsoluteNumber = absolute,
                Title = $"S{seasonNumber:00}E{number:00}",
                AirDate = airDate,
                AddedAt = now,
            });
        }
    }
}
