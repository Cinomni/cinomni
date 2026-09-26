using Cinomni.Kernel.Identifiers;
using Cinomni.Kernel.Messaging;
using Cinomni.Operations.Messaging;
using Cinomni.Operations.Persistence;
using Cinomni.Operations.Retention;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cinomni.Operations.Tests;

/// <summary>
/// Integration tests for the platform retention purge against a real PostgreSQL instance: what it
/// removes, what it must never remove, that it is a no-op on a second run, and that the scheduled
/// job actually reaches the handler.
/// </summary>
public sealed class RetentionTests : IAsyncLifetime
{
    private static readonly TimeSpan OutboxWindow = TimeSpan.FromDays(14);
    private static readonly TimeSpan CompletedWindow = TimeSpan.FromDays(30);
    private static readonly TimeSpan FailedWindow = TimeSpan.FromDays(180);

    private ServiceProvider _provider = null!;

    public async Task InitializeAsync()
    {
        _provider = await OperationsTestHost.CreateAsync(
            "cinomni_test_operations_retention",
            services => services.AddCommand<Tick>("test.tick"),
            retention =>
            {
                retention.OutboxRetention = OutboxWindow;
                retention.CompletedCommandRetention = CompletedWindow;
                retention.FailedCommandRetention = FailedWindow;
                // Small enough that the batching loop runs more than once over the seeded data.
                retention.BatchSize = 2;
            });
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task Purge_removes_old_published_messages_and_terminal_commands_only()
    {
        var now = DateTimeOffset.UtcNow;

        await SeedAsync(dbContext =>
        {
            // Victims.
            dbContext.Outbox.Add(Message("old-published", published: true, at: now - OutboxWindow - TimeSpan.FromDays(1)));
            dbContext.Outbox.Add(Message("old-published-2", published: true, at: now - OutboxWindow - TimeSpan.FromDays(9)));
            dbContext.Outbox.Add(Message("old-published-3", published: true, at: now - OutboxWindow - TimeSpan.FromDays(3)));
            dbContext.Commands.Add(Command("old-completed", CommandState.Completed, now - CompletedWindow - TimeSpan.FromDays(1)));
            dbContext.Commands.Add(Command("ancient-failed", CommandState.Failed, now - FailedWindow - TimeSpan.FromDays(1)));

            // Survivors: an undelivered message is work, not history, however old it is.
            dbContext.Outbox.Add(Message("ancient-unpublished", published: false, at: now - TimeSpan.FromDays(3650)));
            dbContext.Outbox.Add(Message("recent-published", published: true, at: now - TimeSpan.FromDays(1)));
            dbContext.Commands.Add(Command("ancient-queued", CommandState.Queued, now - TimeSpan.FromDays(3650)));
            dbContext.Commands.Add(Command("ancient-running", CommandState.Running, now - TimeSpan.FromDays(3650)));
            dbContext.Commands.Add(Command("recent-completed", CommandState.Completed, now - TimeSpan.FromDays(2)));
            dbContext.Commands.Add(Command("recent-failed", CommandState.Failed, now - CompletedWindow - TimeSpan.FromDays(1)));
        });

        await PurgeAsync();

        var messageKeys = await KeysAsync(dbContext => dbContext.Outbox.Select(m => m.IdempotencyKey));
        Assert.Equal(["ancient-unpublished", "recent-published"], messageKeys.Order().ToArray());

        var commandKeys = await KeysAsync(dbContext => dbContext.Commands.Select(c => c.IdempotencyKey));
        Assert.Equal(
            ["ancient-queued", "ancient-running", "recent-completed", "recent-failed"],
            commandKeys.Order().ToArray());
    }

    [Fact]
    public async Task Purge_is_a_no_op_when_run_again()
    {
        var now = DateTimeOffset.UtcNow;

        await SeedAsync(dbContext =>
        {
            dbContext.Outbox.Add(Message("stale", published: true, at: now - OutboxWindow - TimeSpan.FromDays(2)));
            dbContext.Commands.Add(Command("stale-command", CommandState.Completed, now - CompletedWindow - TimeSpan.FromDays(2)));
        });

        await PurgeAsync();
        await PurgeAsync();

        await using var scope = _provider.CreateAsyncScope();
        var verify = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
        Assert.Equal(0, await verify.Outbox.CountAsync());
        Assert.Equal(0, await verify.Commands.CountAsync());
    }

    /// <summary>
    /// Claiming a batch and deleting it are two statements, and not every retention rule is purely
    /// time-based: Decision exempts an evaluation the moment an administrator confirms a manual
    /// override on it. A row that stops matching in between must therefore survive, which it only
    /// does because the rule is re-applied in the delete rather than trusted from the claim.
    /// </summary>
    [Fact]
    public async Task A_row_that_stops_matching_between_the_claim_and_the_delete_survives()
    {
        var now = DateTimeOffset.UtcNow;
        var queuedAt = now - CompletedWindow - TimeSpan.FromDays(1);

        await SeedAsync(dbContext =>
        {
            dbContext.Commands.Add(Command("rescued", CommandState.Completed, queuedAt));
            dbContext.Commands.Add(Command("doomed", CommandState.Completed, queuedAt));
        });

        var cutoff = now - CompletedWindow;

        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();

        var removed = await RetentionPurge.DeleteInBatchesAsync(
            dbContext.Commands,
            c => c.State == CommandState.Completed && c.QueuedAt < cutoff,
            c => c.Id,
            async (_, token) => await dbContext.Commands
                .Where(c => c.IdempotencyKey == "rescued")
                .ExecuteUpdateAsync(setters => setters.SetProperty(c => c.State, CommandState.Queued), token),
            batchSize: 10);

        Assert.Equal(1, removed);

        var survivors = await dbContext.Commands.AsNoTracking().Select(c => c.IdempotencyKey).ToListAsync();
        Assert.Equal(["rescued"], survivors);
    }

    /// <summary>
    /// The idempotency invariant, as a test rather than a comment: a completed command row is its
    /// own dedup record, so a configuration that frees it before the outbox message that could
    /// re-enqueue it must not start at all.
    /// </summary>
    [Fact]
    public void Completed_command_window_shorter_than_the_outbox_window_fails_at_startup()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var failure = Assert.Throws<InvalidOperationException>(() => services.AddOperations(
            OperationsTestHost.ConnectionStringFor("cinomni_test_operations_retention"),
            retention =>
            {
                retention.OutboxRetention = TimeSpan.FromDays(30);
                retention.CompletedCommandRetention = TimeSpan.FromDays(30);
            }));

        Assert.Contains("CompletedCommandRetention", failure.Message, StringComparison.Ordinal);
        Assert.Contains("OutboxRetention", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Non_positive_batch_size_fails_at_startup(int batchSize)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var failure = Assert.Throws<InvalidOperationException>(() => services.AddOperations(
            OperationsTestHost.ConnectionStringFor("cinomni_test_operations_retention"),
            retention => retention.BatchSize = batchSize));

        Assert.Contains("BatchSize", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>The job is really wired: the scheduler enqueues it and the worker runs the purge.</summary>
    [Fact]
    public async Task Scheduled_job_reaches_the_purge_handler()
    {
        var now = DateTimeOffset.UtcNow;
        await SeedAsync(dbContext =>
            dbContext.Outbox.Add(Message("scheduled-victim", published: true, at: now - OutboxWindow - TimeSpan.FromDays(5))));

        // Upserts the registrations, including operations.retention, due immediately.
        await _provider.RecoverOperationsAsync();

        await using (var scope = _provider.CreateAsyncScope())
        {
            var scheduler = scope.ServiceProvider.GetRequiredService<Scheduler>();
            Assert.True(await scheduler.RunDueJobsAsync() >= 1);
        }

        await using (var scope = _provider.CreateAsyncScope())
        {
            var processor = scope.ServiceProvider.GetRequiredService<CommandProcessor>();
            Assert.True(await processor.ProcessBatchAsync() >= 1);
        }

        await using (var scope = _provider.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
            Assert.False(await dbContext.Outbox.AnyAsync(m => m.IdempotencyKey == "scheduled-victim"));

            var purge = await dbContext.Commands
                .AsNoTracking()
                .SingleAsync(c => c.CommandType == OperationsCommandNames.PurgeRetention);
            Assert.Equal(CommandState.Completed, purge.State);
        }
    }

    private async Task PurgeAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<PurgeOperationsCommand>>();
        var result = await handler.HandleAsync(new PurgeOperationsCommand());
        Assert.True(result.IsSuccess);
    }

    private async Task SeedAsync(Action<OperationsDbContext> seed)
    {
        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
        await dbContext.Outbox.ExecuteDeleteAsync();
        await dbContext.Commands.ExecuteDeleteAsync();
        seed(dbContext);
        await dbContext.SaveChangesAsync();
    }

    private async Task<List<string>> KeysAsync(Func<OperationsDbContext, IQueryable<string>> select)
    {
        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
        return await select(dbContext).AsNoTracking().ToListAsync();
    }

    private static OutboxMessage Message(string key, bool published, DateTimeOffset at) => new()
    {
        Id = Uuid7.New(),
        EventType = "test.event",
        Payload = "{}",
        IdempotencyKey = key,
        OccurredAt = at,
        Published = published,
        PublishedAt = published ? at : null,
    };

    private static QueuedCommand Command(string key, string state, DateTimeOffset queuedAt) => new()
    {
        Id = Uuid7.New(),
        CommandType = "test.tick",
        Payload = "{}",
        IdempotencyKey = key,
        State = state,
        MaxAttempts = 3,
        QueuedAt = queuedAt,
    };

    private sealed record Tick : ICommand;
}
