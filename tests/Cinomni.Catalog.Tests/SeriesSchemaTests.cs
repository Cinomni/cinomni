using Cinomni.Catalog.Contracts;
using Cinomni.Catalog.Persistence;
using Cinomni.Kernel.Identifiers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Catalog.Tests;

/// <summary>
/// Schema tests for the series hierarchy against a real PostgreSQL instance. These assert the
/// constraints the whole slice relies on and that no C# code can enforce: the natural keys a structure
/// sync upserts on, the deliberately FILTERED absolute-number index, and the single cascade path from a
/// work down to its episodes.
/// <para>
/// It runs against its own database on purpose — every <c>TestHost</c> opens with
/// <c>EnsureDeletedAsync</c> and xUnit runs test classes in parallel, so sharing
/// <c>cinomni_test_catalog</c> with <see cref="CatalogFlowTests"/> would drop that suite's database
/// mid-run.
/// </para>
/// </summary>
public sealed class SeriesSchemaTests : IAsyncLifetime
{
    private const string Database = "cinomni_test_catalog_series";

    private ServiceProvider _provider = null!;

    public async Task InitializeAsync() => _provider = await CatalogTestHost.CreateAsync(Database);

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task Two_seasons_with_the_same_number_violate_the_unique_index()
    {
        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var workId = await AddSeriesAsync(dbContext, "The Wire");

        dbContext.Seasons.Add(NewSeason(workId, 1));
        await dbContext.SaveChangesAsync();

        // (work_id, number) is the natural key a structure sync upserts on: two rows for one season
        // would let a later snapshot silently fork the hierarchy.
        dbContext.Seasons.Add(NewSeason(workId, 1));
        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => dbContext.SaveChangesAsync());
        Assert.Contains("ux_seasons_work_number", exception.InnerException!.Message);
    }

    [Fact]
    public async Task Two_episodes_with_the_same_sxxeyy_violate_the_unique_index()
    {
        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var workId = await AddSeriesAsync(dbContext, "Breaking Bad");
        var seasonId = await AddSeasonAsync(dbContext, workId, 1);

        dbContext.Episodes.Add(NewEpisode(workId, seasonId, seasonNumber: 1, number: 5));
        await dbContext.SaveChangesAsync();

        // SxxEyy must resolve to exactly one unit — a duplicate would silently link an asset to an
        // arbitrary one of the two.
        dbContext.Episodes.Add(NewEpisode(workId, seasonId, seasonNumber: 1, number: 5));
        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => dbContext.SaveChangesAsync());
        Assert.Contains("ux_episodes_work_season_number", exception.InnerException!.Message);
    }

    [Fact]
    public async Task Many_episodes_may_have_a_null_absolute_number()
    {
        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var workId = await AddSeriesAsync(dbContext, "Mad Men");
        var seasonId = await AddSeasonAsync(dbContext, workId, 1);

        // Most non-anime providers publish no absolute number at all. Without the
        // `WHERE absolute_number IS NOT NULL` filter, the unique index would reject the second episode
        // of every such series.
        for (var number = 1; number <= 5; number++)
        {
            dbContext.Episodes.Add(NewEpisode(workId, seasonId, seasonNumber: 1, number: number));
        }

        await dbContext.SaveChangesAsync();

        var stored = await dbContext.Episodes.Where(e => e.WorkId == workId).ToListAsync();
        Assert.Equal(5, stored.Count);
        Assert.All(stored, episode => Assert.Null(episode.AbsoluteNumber));
    }

    [Fact]
    public async Task Two_episodes_with_the_same_absolute_number_violate_the_unique_index()
    {
        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var workId = await AddSeriesAsync(dbContext, "One Piece");
        var seasonId = await AddSeasonAsync(dbContext, workId, 1);

        var first = NewEpisode(workId, seasonId, seasonNumber: 1, number: 1);
        first.AbsoluteNumber = 123;
        dbContext.Episodes.Add(first);
        await dbContext.SaveChangesAsync();

        // The filter narrows the index; it does not disable it. Absolute numbering is how anime
        // releases resolve, so a duplicate would be an ambiguous match.
        var second = NewEpisode(workId, seasonId, seasonNumber: 1, number: 2);
        second.AbsoluteNumber = 123;
        dbContext.Episodes.Add(second);

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => dbContext.SaveChangesAsync());
        Assert.Contains("ux_episodes_work_absolute_number", exception.InnerException!.Message);
    }

    [Fact]
    public async Task Deleting_a_work_cascades_seasons_and_episodes()
    {
        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var workId = await AddSeriesAsync(dbContext, "Deadwood");
        var seasonId = await AddSeasonAsync(dbContext, workId, 1);
        dbContext.Episodes.Add(NewEpisode(workId, seasonId, seasonNumber: 1, number: 1));
        dbContext.Episodes.Add(NewEpisode(workId, seasonId, seasonNumber: 1, number: 2));
        await dbContext.SaveChangesAsync();

        // A raw DELETE, so this proves the database cascade rather than EF's change tracker:
        // works → seasons → episodes, one path only (episodes.work_id is a plain column, not a
        // second foreign key — two cascade paths to one row is a PostgreSQL error).
        await dbContext.Works.Where(w => w.Id == workId).ExecuteDeleteAsync();

        Assert.Empty(await dbContext.Seasons.Where(s => s.WorkId == workId).ToListAsync());
        Assert.Empty(await dbContext.Episodes.Where(e => e.WorkId == workId).ToListAsync());
    }

    [Fact]
    public async Task A_series_persists_its_structure_provider_and_rollups()
    {
        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var workId = await AddSeriesAsync(dbContext, "The Sopranos");

        var work = await dbContext.Works.SingleAsync(w => w.Id == workId);
        // Movie rows migrate with no backfill: the counters default to 0 and the claim is unset.
        Assert.Null(work.StructureProvider);
        Assert.Equal(0, work.EpisodeCount);
        Assert.Equal(0, work.AvailableEpisodeCount);

        work.StructureProvider = "tvdb";
        work.EpisodeCount = 86;
        work.AvailableEpisodeCount = 3;
        await dbContext.SaveChangesAsync();

        dbContext.ChangeTracker.Clear();
        var reloaded = await dbContext.Works.AsNoTracking().SingleAsync(w => w.Id == workId);
        Assert.Equal("tvdb", reloaded.StructureProvider);
        Assert.Equal(86, reloaded.EpisodeCount);
        Assert.Equal(3, reloaded.AvailableEpisodeCount);
    }

    private static async Task<Guid> AddSeriesAsync(CatalogDbContext dbContext, string title)
    {
        var work = new Work
        {
            Id = Uuid7.New(),
            Kind = WorkKind.Series,
            Title = title,
            SortTitle = title.ToLowerInvariant(),
            Status = WorkStatus.Continuing,
            AddedAt = DateTimeOffset.UtcNow,
        };

        dbContext.Works.Add(work);
        await dbContext.SaveChangesAsync();
        return work.Id;
    }

    private static async Task<Guid> AddSeasonAsync(CatalogDbContext dbContext, Guid workId, int number)
    {
        var season = NewSeason(workId, number);
        dbContext.Seasons.Add(season);
        await dbContext.SaveChangesAsync();
        return season.Id;
    }

    private static Season NewSeason(Guid workId, int number) => new()
    {
        Id = Uuid7.New(),
        WorkId = workId,
        Number = number,
        AddedAt = DateTimeOffset.UtcNow,
    };

    private static Episode NewEpisode(Guid workId, Guid seasonId, int seasonNumber, int number) => new()
    {
        Id = Uuid7.New(),
        SeasonId = seasonId,
        WorkId = workId,
        SeasonNumber = seasonNumber,
        Number = number,
        AddedAt = DateTimeOffset.UtcNow,
    };
}
