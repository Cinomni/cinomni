using Cinomni.Discovery.Contracts;
using Cinomni.Discovery.Persistence;
using Cinomni.Kernel.Identifiers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cinomni.Discovery.Tests;

/// <summary>
/// The expand/contract migration exercised against a <b>populated</b> database, which is the only
/// way to prove it is safe on a real installation. The database is taken to the pre-partition
/// migration, rows are inserted through the shape that existed then, and only afterwards is the rest
/// of the migration applied.
/// <para>
/// It asserts the four things that can silently go wrong: rows lost in the copy, <c>found_at</c>
/// backfilled from the wrong side (the scaffolded version defaulted it to year 1 and would have aged
/// everything out at once), the tables not actually becoming partitioned, and rows landing in a month
/// they do not belong to. It then reads them back through the public interface, and finally rolls the
/// migration back down and checks the heap shape returns with its data.
/// </para>
/// </summary>
public sealed class SearchPartitionMigrationTests : IAsyncLifetime
{
    /// <summary>The last migration before partitioning — the shape an existing installation is on.</summary>
    private const string PrePartitionMigration = "20260728200221_AddSeriesSearchContext";

    private static readonly DateTimeOffset ExecutionStartedAt = new(2026, 3, 14, 9, 30, 0, TimeSpan.Zero);

    private readonly Guid _executionId = Uuid7.New();
    private readonly Guid[] _resultIds = [Uuid7.New(), Uuid7.New(), Uuid7.New()];

    private ServiceProvider _provider = null!;

    public async Task InitializeAsync() =>
        _provider = await DiscoveryTestHost.CreateAsync(
            "cinomni_test_discovery_partition",
            services =>
            {
                services.AddSingleton<FakeIndexerCatalog>();
                services.AddSingleton<Indexers.IIndexerClient, FakeIndexerClient>();
            },
            discoveryMigrationTarget: PrePartitionMigration);

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task Populated_tables_survive_the_move_to_monthly_partitions()
    {
        await SeedPrePartitionRowsAsync();
        await MigrateToHeadAsync();

        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>();

        // 1. Nothing was lost, and the execution is readable through the current model.
        var execution = await dbContext.SearchExecutions.AsNoTracking().SingleAsync(e => e.Id == _executionId);
        Assert.Equal(ExecutionStartedAt, execution.StartedAt);
        Assert.Equal(_resultIds.Length, execution.ResultCount);

        var results = await dbContext.SearchResults
            .AsNoTracking()
            .Where(r => r.ExecutionId == _executionId)
            .ToListAsync();
        Assert.Equal(_resultIds.Length, results.Count);

        // 2. found_at was backfilled from the parent execution, not defaulted.
        Assert.All(results, r => Assert.Equal(ExecutionStartedAt, r.FoundAt));

        // 3. Both tables are genuinely partitioned now ('p' is a partitioned table).
        Assert.Equal("p", await RelKindAsync(dbContext, "search_executions"));
        Assert.Equal("p", await RelKindAsync(dbContext, "search_results"));

        // 4. The rows sit in the partition for their own month.
        var expectedExecutionPartition = SearchPartitions.PartitionName(
            SearchHistoryTable.Executions, DateOnly.FromDateTime(ExecutionStartedAt.UtcDateTime));
        var expectedResultPartition = SearchPartitions.PartitionName(
            SearchHistoryTable.Results, DateOnly.FromDateTime(ExecutionStartedAt.UtcDateTime));

        Assert.Equal(
            $"{SearchPartitions.Schema}.{expectedExecutionPartition}",
            await PartitionOfAsync(dbContext, "search_executions", _executionId));
        Assert.Equal(
            $"{SearchPartitions.Schema}.{expectedResultPartition}",
            await PartitionOfAsync(dbContext, "search_results", _resultIds[0]));
    }

    [Fact]
    public async Task Migrated_results_are_still_readable_through_the_public_interface()
    {
        await SeedPrePartitionRowsAsync();
        await MigrateToHeadAsync();

        await using var scope = _provider.CreateAsyncScope();
        var results = scope.ServiceProvider.GetRequiredService<IReleaseSearchResults>();

        var candidates = await results.GetResultsAsync(new SearchExecutionId(_executionId));
        Assert.Equal(_resultIds.Length, candidates.Count);
        Assert.Contains(candidates, c => c.Guid == "legacy-0");

        var context = await results.GetRequestContextAsync(new SearchExecutionId(_executionId));
        Assert.NotNull(context);
        Assert.Equal("Legacy search", context.Term);
    }

    [Fact]
    public async Task The_migration_rolls_back_to_the_heap_shape_with_its_data()
    {
        await SeedPrePartitionRowsAsync();
        await MigrateToHeadAsync();
        await MigrateToAsync(PrePartitionMigration);

        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>();

        // 'r' is an ordinary heap table: the partitioning is gone.
        Assert.Equal("r", await RelKindAsync(dbContext, "search_executions"));
        Assert.Equal("r", await RelKindAsync(dbContext, "search_results"));

        var executions = await CountAsync(dbContext, "SELECT count(*) AS \"Value\" FROM discovery.search_executions");
        var resultRows = await CountAsync(dbContext, "SELECT count(*) AS \"Value\" FROM discovery.search_results");
        Assert.Equal(1, executions);
        Assert.Equal(_resultIds.Length, resultRows);

        // found_at only existed as a partition key, so it is gone with the partitioning.
        var columns = await dbContext.Database
            .SqlQuery<string>(
                $"""
                 SELECT column_name AS "Value"
                 FROM information_schema.columns
                 WHERE table_schema = 'discovery' AND table_name = 'search_results'
                 """)
            .ToListAsync();
        Assert.DoesNotContain("found_at", columns);
    }

    /// <summary>Inserts through the pre-partition shape: no <c>found_at</c>, foreign key still present.</summary>
    private async Task SeedPrePartitionRowsAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>();

        await dbContext.Database.ExecuteSqlAsync(
            $"""
             INSERT INTO discovery.search_executions
                 (id, term, year, content_kind, started_at, completed_at, result_count, requested_unit_ids)
             VALUES ({_executionId}, {"Legacy search"}, {2026}, {"Movie"},
                     {ExecutionStartedAt}, {ExecutionStartedAt.AddSeconds(4)}, {_resultIds.Length},
                     {Array.Empty<Guid>()})
             """);

        for (var index = 0; index < _resultIds.Length; index++)
        {
            await dbContext.Database.ExecuteSqlAsync(
                $"""
                 INSERT INTO discovery.search_results
                     (id, execution_id, release_guid, title, download_url, protocol, size_bytes, indexer_name)
                 VALUES ({_resultIds[index]}, {_executionId}, {$"legacy-{index}"}, {$"Legacy release {index}"},
                         {"https://indexer.example/download"}, {"Torrent"}, {1024L}, {"Alpha"})
                 """);
        }
    }

    private Task MigrateToHeadAsync() => MigrateToAsync(target: null);

    private async Task MigrateToAsync(string? target)
    {
        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>();
        await dbContext.GetService<IMigrator>().MigrateAsync(target);
    }

    private static async Task<string> RelKindAsync(DiscoveryDbContext dbContext, string table)
    {
        var kinds = await dbContext.Database
            .SqlQuery<char>(
                $"""
                 SELECT c.relkind AS "Value"
                 FROM pg_class AS c
                 JOIN pg_namespace AS n ON n.oid = c.relnamespace
                 WHERE n.nspname = {SearchPartitions.Schema} AND c.relname = {table}
                 """)
            .ToListAsync();

        return kinds.Single().ToString();
    }

    private static async Task<string> PartitionOfAsync(DiscoveryDbContext dbContext, string table, Guid id)
    {
        var sql = table == "search_executions"
            ? "SELECT tableoid::regclass::text AS \"Value\" FROM discovery.search_executions WHERE id = @id"
            : "SELECT tableoid::regclass::text AS \"Value\" FROM discovery.search_results WHERE id = @id";

        var names = await dbContext.Database
            .SqlQueryRaw<string>(sql, new Npgsql.NpgsqlParameter("id", id))
            .ToListAsync();

        return names.Single();
    }

    private static async Task<int> CountAsync(DiscoveryDbContext dbContext, string sql)
    {
        var counts = await dbContext.Database.SqlQueryRaw<long>(sql).ToListAsync();
        return (int)counts.Single();
    }
}
