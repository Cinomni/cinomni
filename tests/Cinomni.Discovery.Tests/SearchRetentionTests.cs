using System.Globalization;
using Cinomni.Discovery.Contracts;
using Cinomni.Discovery.Messaging;
using Cinomni.Discovery.Persistence;
using Cinomni.Kernel.Identifiers;
using Cinomni.Kernel.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cinomni.Discovery.Tests;

/// <summary>
/// Integration tests for the search-history retention job against a real PostgreSQL instance: a
/// whole expired month disappears with both its tables, a recent month survives, the months ahead
/// are pre-created, the DEFAULT partition stays empty, and a partition this code did not generate is
/// never dropped.
/// </summary>
public sealed class SearchRetentionTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync() =>
        _provider = await DiscoveryTestHost.CreateAsync(
            "cinomni_test_discovery_retention",
            services =>
            {
                services.AddSingleton<FakeIndexerCatalog>();
                services.AddSingleton<Indexers.IIndexerClient, FakeIndexerClient>();
            },
            retention => retention.SearchRetention = TimeSpan.FromDays(30));

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task Expired_month_is_dropped_with_its_results_and_recent_months_survive()
    {
        var now = DateTimeOffset.UtcNow;
        var expired = now.AddMonths(-4);

        var expiredId = await SeedSearchAsync(expired, resultCount: 3);
        var recentId = await SeedSearchAsync(now, resultCount: 2);

        await PurgeAsync();

        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>();

        Assert.False(await dbContext.SearchExecutions.AnyAsync(e => e.Id == expiredId));
        // The foreign key is gone, so this is the assertion that co-partitioning really keeps the
        // two tables in step: results must not outlive their execution.
        Assert.False(await dbContext.SearchResults.AnyAsync(r => r.ExecutionId == expiredId));

        Assert.True(await dbContext.SearchExecutions.AnyAsync(e => e.Id == recentId));
        Assert.Equal(2, await dbContext.SearchResults.CountAsync(r => r.ExecutionId == recentId));
    }

    [Fact]
    public async Task Purge_pre_creates_the_next_months_and_leaves_the_default_partition_empty()
    {
        await SeedSearchAsync(DateTimeOffset.UtcNow, resultCount: 1);

        await PurgeAsync();

        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>();

        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);
        for (var offset = 0; offset <= SearchPartitions.MonthsCreatedAhead; offset++)
        {
            var month = today.AddMonths(offset);
            Assert.True(
                await PartitionExistsAsync(dbContext, SearchPartitions.PartitionName(SearchHistoryTable.Executions, month)),
                $"Expected the executions partition for {month:yyyy-MM} to exist.");
            Assert.True(
                await PartitionExistsAsync(dbContext, SearchPartitions.PartitionName(SearchHistoryTable.Results, month)),
                $"Expected the results partition for {month:yyyy-MM} to exist.");
        }

        var (defaultExecutions, defaultResults) =
            await SearchPartitions.CountDefaultRowsAsync(dbContext);
        Assert.Equal(0, defaultExecutions);
        Assert.Equal(0, defaultResults);
    }

    [Fact]
    public async Task Running_the_purge_again_changes_nothing()
    {
        var now = DateTimeOffset.UtcNow;
        await SeedSearchAsync(now.AddMonths(-4), resultCount: 2);
        var survivorId = await SeedSearchAsync(now, resultCount: 2);

        await PurgeAsync();
        await PurgeAsync();

        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>();
        Assert.Equal(1, await dbContext.SearchExecutions.CountAsync());
        Assert.Equal(2, await dbContext.SearchResults.CountAsync(r => r.ExecutionId == survivorId));
    }

    /// <summary>
    /// The configured window is a floor, not a ceiling, and this pins the residue so the behaviour and
    /// <c>SearchRetentionOptions</c> cannot drift apart. A month is dropped only once its <i>last</i>
    /// instant has aged past the window, so a search can outlive the cutoff by nearly a month — never
    /// the other way round, which is the direction that would matter.
    /// </summary>
    [Fact]
    public async Task A_month_is_kept_until_its_last_instant_has_aged_past_the_window()
    {
        var lastDayOfDecember = new DateTimeOffset(2025, 12, 31, 23, 0, 0, TimeSpan.Zero);
        var earlyJanuary = new DateTimeOffset(2026, 1, 2, 1, 0, 0, TimeSpan.Zero);

        var december = await SeedSearchAsync(lastDayOfDecember, resultCount: 1);
        var january = await SeedSearchAsync(earlyJanuary, resultCount: 1);

        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>();

        // A cutoff of 20 January: both searches are older than it, but only December's month is
        // entirely behind the cutoff.
        await SearchPartitions.DropExpiredAsync(dbContext, new DateOnly(2026, 1, 20));

        Assert.False(await dbContext.SearchExecutions.AnyAsync(e => e.Id == december));
        Assert.False(await dbContext.SearchResults.AnyAsync(r => r.ExecutionId == december));
        Assert.True(await dbContext.SearchExecutions.AnyAsync(e => e.Id == january));
    }

    /// <summary>
    /// The recovery case that matters most in this module: PostgreSQL revalidates the DEFAULT
    /// partition when a new range partition is attached, and refuses if a row already in DEFAULT
    /// belongs to the new range. That state is reachable (maintenance disabled for months, a clock
    /// jump), and partition maintenance runs on the Host's startup path — so if it escaped, one stray
    /// row would keep the whole installation from booting, for ever, until somebody ran SQL by hand.
    /// It must be reported and stepped over instead.
    /// </summary>
    [Fact]
    public async Task A_row_in_the_default_partition_does_not_stop_startup()
    {
        var blockedMonth = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime).AddMonths(1);
        var blockedInstant = new DateTimeOffset(
            blockedMonth.Year, blockedMonth.Month, 15, 12, 0, 0, TimeSpan.Zero);

        // Remove next month's (empty) partitions so a row for that month falls into DEFAULT, exactly
        // as it would on an installation whose retention job had not run for months.
        await DropPartitionsForAsync(blockedMonth);
        var strandedId = await SeedSearchAsync(blockedInstant, resultCount: 2, ensurePartitions: false);

        await using (var scope = _provider.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>();
            var (executions, results) = await SearchPartitions.CountDefaultRowsAsync(dbContext);
            Assert.Equal(1, executions);
            Assert.Equal(2, results);

            // The conflict really happens, and it is reported rather than thrown: without this the
            // rest of the test would pass for the wrong reason.
            var maintenance = await SearchPartitions.EnsureAsync(
                dbContext, DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime));
            Assert.Contains(
                SearchPartitions.PartitionName(SearchHistoryTable.Executions, blockedMonth),
                maintenance.Blocked);
            Assert.Contains(
                SearchPartitions.PartitionName(SearchHistoryTable.Results, blockedMonth),
                maintenance.Blocked);
        }

        // The Host's startup path. It must complete: the stray row is a condition to report, never one
        // that stops Identity, Playback and everything else from starting.
        await _provider.MigrateDiscoveryAsync();

        // And the daily job must keep working too, including the drop that reclaims space.
        var expiredId = await SeedSearchAsync(DateTimeOffset.UtcNow.AddMonths(-4), resultCount: 1);
        await PurgeAsync();

        await using (var scope = _provider.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>();

            Assert.False(await dbContext.SearchExecutions.AnyAsync(e => e.Id == expiredId));
            // Nothing was silently discarded: the stranded row is still readable where it landed.
            Assert.True(await dbContext.SearchExecutions.AnyAsync(e => e.Id == strandedId));
            Assert.Equal(2, await dbContext.SearchResults.CountAsync(r => r.ExecutionId == strandedId));

            var months = await SearchPartitions.DefaultMonthsAsync(dbContext);
            Assert.Contains(blockedMonth.ToString("yyyy_MM", CultureInfo.InvariantCulture), months);
        }
    }

    /// <summary>
    /// Abuse case for the one place in Cinomni that emits DDL. A partition whose name this class
    /// could not have produced — including anything carrying SQL punctuation — is evidence, not an
    /// identifier: it is skipped, never interpolated into a DROP.
    /// </summary>
    [Fact]
    public async Task A_partition_this_code_did_not_name_is_never_dropped()
    {
        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>();

        // Two hand-made partitions in a long-expired range: one with a plausible but non-generated
        // suffix, one whose name contains punctuation a naive DROP would happily execute.
        await dbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE discovery."search_results_1999_01_kept"
                PARTITION OF discovery.search_results
                FOR VALUES FROM ('1999-01-01 00:00:00+00') TO ('1999-02-01 00:00:00+00')
            """);
        await dbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE discovery."search_results_1999_02""; DROP TABLE discovery.indexers; --"
                PARTITION OF discovery.search_results
                FOR VALUES FROM ('1999-02-01 00:00:00+00') TO ('1999-03-01 00:00:00+00')
            """);

        var dropped = await SearchPartitions.DropExpiredAsync(
            dbContext, DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime));

        Assert.DoesNotContain(dropped, name => name.Contains("1999", StringComparison.Ordinal));
        Assert.True(await PartitionExistsAsync(dbContext, "search_results_1999_01_kept"));
        Assert.True(await TableExistsAsync(dbContext, "indexers"));
    }

    [Fact]
    public void Generated_partition_names_are_plain_lowercase_identifiers()
    {
        var month = new DateOnly(2026, 1, 1);

        for (var offset = 0; offset < 36; offset++)
        {
            foreach (var table in new[] { SearchHistoryTable.Executions, SearchHistoryTable.Results })
            {
                var name = SearchPartitions.PartitionName(table, month.AddMonths(offset));
                Assert.All(name, c => Assert.True(
                    c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_',
                    $"'{name}' contains a character that must never reach DDL."));
            }
        }
    }

    private async Task PurgeAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<PurgeSearchesCommand>>();
        Assert.True((await handler.HandleAsync(new PurgeSearchesCommand())).IsSuccess);
    }

    /// <summary>Removes both partitions of a month, leaving its rows to fall into DEFAULT.</summary>
    private async Task DropPartitionsForAsync(DateOnly month)
    {
        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>();

        foreach (var table in new[] { SearchHistoryTable.Results, SearchHistoryTable.Executions })
        {
#pragma warning disable EF1002 // Identifiers cannot be parameterised; the name is generated, not input.
            await dbContext.Database.ExecuteSqlRawAsync(
                $"DROP TABLE IF EXISTS {SearchPartitions.Schema}.{SearchPartitions.PartitionName(table, month)}");
#pragma warning restore EF1002
        }
    }

    private async Task<Guid> SeedSearchAsync(DateTimeOffset at, int resultCount, bool ensurePartitions = true)
    {
        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>();

        // The month has to exist before a row of that month can be routed into it — unless the test is
        // deliberately writing into DEFAULT.
        if (ensurePartitions)
        {
            await SearchPartitions.EnsureAsync(dbContext, DateOnly.FromDateTime(at.UtcDateTime));
        }

        var executionId = Uuid7.New();
        dbContext.SearchExecutions.Add(new SearchExecution
        {
            Id = executionId,
            Term = "Seeded",
            ContentKind = "Movie",
            StartedAt = at,
            CompletedAt = at,
            ResultCount = resultCount,
        });

        for (var index = 0; index < resultCount; index++)
        {
            dbContext.SearchResults.Add(new SearchResult
            {
                Id = Uuid7.New(),
                ExecutionId = executionId,
                FoundAt = at,
                ReleaseGuid = $"{executionId}:{index}",
                Title = "Seeded release",
                DownloadUrl = "https://indexer.example/download",
                Protocol = ReleaseProtocol.Torrent,
                SizeBytes = 1024,
                IndexerName = "Alpha",
            });
        }

        await dbContext.SaveChangesAsync();
        return executionId;
    }

    private static async Task<bool> PartitionExistsAsync(DiscoveryDbContext dbContext, string name) =>
        await TableExistsAsync(dbContext, name);

    private static async Task<bool> TableExistsAsync(DiscoveryDbContext dbContext, string name)
    {
        var qualified = $"{SearchPartitions.Schema}.\"{name}\"";
        var present = await dbContext.Database
            .SqlQuery<bool>($"SELECT to_regclass({qualified}) IS NOT NULL AS \"Value\"")
            .ToListAsync();
        return present.Count > 0 && present[0];
    }
}
