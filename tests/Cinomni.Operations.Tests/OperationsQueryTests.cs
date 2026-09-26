using Cinomni.Kernel.Identifiers;
using Cinomni.Operations.Diagnostics;
using Cinomni.Operations.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cinomni.Operations.Tests;

/// <summary>
/// Integration tests for the read-only projections behind <c>/api/operations</c>, against a real
/// PostgreSQL instance. Covers the two guarantees the operator console depends on: an empty
/// installation answers rather than throwing, and a caller cannot widen the failed-command list
/// past the server-side ceiling by asking for more.
/// </summary>
public sealed class OperationsQueryTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync()
    {
        // OperationsQuery.GetRetention() now reads through ILiveOptions<RetentionOptions>, whose binder
        // resolves each key from the settings store, then IConfiguration, then the RetentionOptions
        // instance below only as a last resort — exactly the same chain RetentionConfiguration.Operations
        // uses in the real Host. A raw C# callback alone (as before) is invisible to that chain, so the
        // same values are also supplied as configuration here, matching how production really wires them.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Retention:OutboxRetention"] = "14.00:00:00",
                ["Retention:CompletedCommandRetention"] = "30.00:00:00",
                ["Retention:FailedCommandRetention"] = "180.00:00:00",
                ["Retention:BatchSize"] = "2500",
                ["Retention:Interval"] = "06:00:00",
            })
            .Build();

        _provider = await OperationsTestHost.CreateAsync(
            "cinomni_test_operations_query",
            services => { },
            retention =>
            {
                retention.OutboxRetention = TimeSpan.FromDays(14);
                retention.CompletedCommandRetention = TimeSpan.FromDays(30);
                retention.FailedCommandRetention = TimeSpan.FromDays(180);
                retention.BatchSize = 2_500;
                retention.Interval = TimeSpan.FromHours(6);
            },
            configuration: configuration);
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task Queue_snapshot_reports_an_empty_installation_without_failing()
    {
        await using var scope = _provider.CreateAsyncScope();
        var query = scope.ServiceProvider.GetRequiredService<OperationsQuery>();

        var snapshot = await query.GetQueueSnapshotAsync();

        Assert.Equal(0, snapshot.OutboxPending);
        Assert.Equal(TimeSpan.Zero, snapshot.OldestOutboxAge);
        // Every lifecycle state is reported, even at zero, so a dashboard reads "none" rather than
        // "no data" for an installation that has never done anything yet.
        Assert.Equal(0, snapshot.CommandsByState[CommandState.Queued]);
        Assert.Equal(0, snapshot.CommandsByState[CommandState.Running]);
        Assert.Equal(0, snapshot.CommandsByState[CommandState.Completed]);
        Assert.Equal(0, snapshot.CommandsByState[CommandState.Failed]);
    }

    [Fact]
    public async Task Failed_commands_are_capped_to_the_requested_limit_newest_first()
    {
        var now = DateTimeOffset.UtcNow;

        await SeedAsync(dbContext =>
        {
            dbContext.Commands.Add(FailedCommand("oldest", now - TimeSpan.FromHours(3)));
            dbContext.Commands.Add(FailedCommand("middle", now - TimeSpan.FromHours(2)));
            dbContext.Commands.Add(FailedCommand("newest", now - TimeSpan.FromHours(1)));
        });

        await using var scope = _provider.CreateAsyncScope();
        var query = scope.ServiceProvider.GetRequiredService<OperationsQuery>();

        // Three rows exist; asking for two must return exactly two — the two most recent — rather
        // than the caller's request being trusted as a hint.
        var failed = await query.GetFailedCommandsAsync(limit: 2);

        Assert.Equal(2, failed.Count);
        Assert.Equal(["newest", "middle"], failed.Select(command => command.CommandType).ToArray());
    }

    [Fact]
    public async Task A_limit_above_the_server_side_ceiling_never_asks_the_database_for_more_than_the_ceiling()
    {
        var now = DateTimeOffset.UtcNow;

        await SeedAsync(dbContext => dbContext.Commands.Add(FailedCommand("only-one", now)));

        await using var scope = _provider.CreateAsyncScope();
        var query = scope.ServiceProvider.GetRequiredService<OperationsQuery>();

        // A caller asking for far more than the ceiling gets whatever exists (one row here) rather
        // than an error; FailedCommandsPagingTests proves the ceiling itself is enforced regardless
        // of how much data exists.
        var failed = await query.GetFailedCommandsAsync(limit: 1_000_000);

        Assert.Single(failed);
        Assert.True(1_000_000 > FailedCommandsPaging.MaxLimit);
    }

    [Fact]
    public async Task Failed_command_projection_never_carries_the_payload()
    {
        var now = DateTimeOffset.UtcNow;

        await SeedAsync(dbContext =>
        {
            var command = FailedCommand(
                "carries-secret-detail", now, payload: """{"infrastructureDetail":"internal-hostname:9999"}""");
            dbContext.Commands.Add(command);
        });

        await using var scope = _provider.CreateAsyncScope();
        var query = scope.ServiceProvider.GetRequiredService<OperationsQuery>();

        var failed = Assert.Single(await query.GetFailedCommandsAsync(limit: 10));

        // FailedCommandSummary has no Payload member at all (compile-time), and this asserts the
        // fields it does carry stay limited to what an operator console is meant to see.
        Assert.Equal("carries-secret-detail", failed.CommandType);
        Assert.Equal(5, failed.Attempts);
        Assert.Equal("boom", failed.Error);
    }

    [Fact]
    public async Task Jobs_are_projected_with_no_outcome_field()
    {
        var now = DateTimeOffset.UtcNow;

        await using (var scope = _provider.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
            dbContext.ScheduledJobs.Add(new ScheduledJob
            {
                Name = "operations.retention",
                CommandType = "operations.purge-retention",
                IntervalSeconds = 3600,
                LastRun = now - TimeSpan.FromMinutes(10),
                NextDue = now + TimeSpan.FromMinutes(50),
                Enabled = true,
            });
            await dbContext.SaveChangesAsync();
        }

        await using var verify = _provider.CreateAsyncScope();
        var query = verify.ServiceProvider.GetRequiredService<OperationsQuery>();

        var job = Assert.Single(await query.GetJobsAsync());

        Assert.Equal("operations.retention", job.Name);
        Assert.Equal("operations.purge-retention", job.CommandType);
        Assert.Equal(3600, job.IntervalSeconds);
        Assert.True(job.Enabled);
        Assert.NotNull(job.LastRun);
    }

    [Fact]
    public async Task Retention_is_echoed_from_the_frozen_options_not_recomputed()
    {
        await using var scope = _provider.CreateAsyncScope();
        var query = scope.ServiceProvider.GetRequiredService<OperationsQuery>();

        var retention = query.GetRetention();

        Assert.Equal(TimeSpan.FromDays(14).TotalSeconds, retention.OutboxRetentionSeconds);
        Assert.Equal(TimeSpan.FromDays(30).TotalSeconds, retention.CompletedCommandRetentionSeconds);
        Assert.Equal(TimeSpan.FromDays(180).TotalSeconds, retention.FailedCommandRetentionSeconds);
        Assert.Equal(2_500, retention.BatchSize);
        Assert.Equal(TimeSpan.FromHours(6).TotalSeconds, retention.IntervalSeconds);
    }

    private async Task SeedAsync(Action<OperationsDbContext> seed)
    {
        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
        await dbContext.Commands.ExecuteDeleteAsync();
        seed(dbContext);
        await dbContext.SaveChangesAsync();
    }

    private static QueuedCommand FailedCommand(string key, DateTimeOffset lastAttemptAt, string payload = "{}") => new()
    {
        Id = Uuid7.New(),
        CommandType = key,
        Payload = payload,
        IdempotencyKey = key,
        State = CommandState.Failed,
        Attempts = 5,
        MaxAttempts = 5,
        QueuedAt = lastAttemptAt - TimeSpan.FromMinutes(1),
        LastAttemptAt = lastAttemptAt,
        Error = "boom",
    };
}
