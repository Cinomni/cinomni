using System.Collections.Concurrent;
using Cinomni.Kernel.Messaging;
using Cinomni.Kernel.Results;
using Cinomni.Operations.Messaging;
using Cinomni.Operations.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cinomni.Operations.Tests;

/// <summary>
/// Integration tests for the recoverable command queue against a real PostgreSQL instance:
/// successful execution, deduplication, retry-on-failure, and startup recovery.
/// </summary>
public sealed class CommandQueueTests : IAsyncLifetime
{
    private readonly CommandSink _sink = new();
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync()
    {
        _provider = await OperationsTestHost.CreateAsync("cinomni_test_commands", services =>
        {
            services.AddSingleton(_sink);
            services.AddCommand<DoThing>("test.do-thing");
            services.AddScoped<ICommandHandler<DoThing>, DoThingHandler>();
            services.AddCommand<FailThing>("test.fail-thing");
            services.AddScoped<ICommandHandler<FailThing>, FailThingHandler>();
            services.AddScoped<ICommandExhaustedHandler<FailThing>, FailThingExhaustedHandler>();
            services.AddCommand<TimeOutThing>("test.time-out-thing");
            services.AddScoped<ICommandHandler<TimeOutThing>, TimeOutThingHandler>();
            services.AddCommand<PeekThing>("test.peek-thing");
            services.AddScoped<ICommandHandler<PeekThing>, PeekThingHandler>();
        });
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task Enqueued_command_runs_once_and_is_completed()
    {
        var thingId = Guid.NewGuid();
        await EnqueueAsync(new DoThing(thingId), $"do:{thingId}");

        var processed = await ProcessBatchAsync();

        Assert.Equal(1, processed);
        Assert.Contains(thingId, _sink.Handled);

        var command = await SingleCommandAsync();
        Assert.Equal(CommandState.Completed, command.State);
        Assert.Equal(1, command.Attempts);
    }

    [Fact]
    public async Task Enqueuing_the_same_key_twice_is_deduplicated()
    {
        var thingId = Guid.NewGuid();
        await EnqueueAsync(new DoThing(thingId), "same-key");
        await EnqueueAsync(new DoThing(thingId), "same-key");

        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
        Assert.Equal(1, await dbContext.Commands.CountAsync());
    }

    [Fact]
    public async Task Failing_command_is_requeued_with_backoff()
    {
        await EnqueueAsync(new FailThing(), "fail-once");

        var processed = await ProcessBatchAsync();

        Assert.Equal(1, processed);
        var command = await SingleCommandAsync();
        Assert.Equal(CommandState.Queued, command.State);
        Assert.Equal(1, command.Attempts);
        Assert.NotNull(command.RunAfter);
        Assert.Equal("boom", command.Error);
    }

    [Fact]
    public async Task Recovery_requeues_commands_stranded_running()
    {
        await EnqueueAsync(new DoThing(Guid.NewGuid()), "stranded");

        // Simulate a crash mid-execution: leave the command Running.
        await using (var scope = _provider.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
            await dbContext.Database.ExecuteSqlAsync(
                $"UPDATE operations.command SET state = 'Running'");
        }

        await using (var scope = _provider.CreateAsyncScope())
        {
            var processor = scope.ServiceProvider.GetRequiredService<CommandProcessor>();
            await processor.RecoverAsync();
        }

        var command = await SingleCommandAsync();
        Assert.Equal(CommandState.Queued, command.State);
    }

    [Fact]
    public async Task A_handler_that_times_out_is_retried_and_does_not_strand_the_rest_of_the_batch()
    {
        // An HttpClient timeout surfaces as TaskCanceledException, which is an OperationCanceledException
        // — but nobody is shutting down. Let through, it escaped the batch before the outcome was saved:
        // this command and every one after it stayed Running until the next restart, and the attempt was
        // never counted, so a slow dependency made the command a poison that stranded its batch again
        // after every restart.
        var thingId = Guid.NewGuid();
        await EnqueueAsync(new TimeOutThing(), "times-out");
        await EnqueueAsync(new DoThing(thingId), "after-it");

        var processed = await ProcessBatchAsync();

        Assert.Equal(2, processed);
        Assert.Contains(thingId, _sink.Handled);

        var commands = await AllCommandsAsync();
        var timedOut = Assert.Single(commands, c => c.IdempotencyKey == "times-out");
        Assert.Equal(CommandState.Queued, timedOut.State);
        Assert.Equal(1, timedOut.Attempts);
        Assert.NotNull(timedOut.RunAfter);
        Assert.Equal(CommandState.Completed, Assert.Single(commands, c => c.IdempotencyKey == "after-it").State);
    }

    [Fact]
    public async Task An_attempt_is_counted_when_the_command_is_claimed_not_when_it_finishes()
    {
        // Counted at the claim and committed with it, so a handler that takes the process down with it
        // still spends an attempt — otherwise such a command would be retried after every restart for ever.
        await EnqueueAsync(new PeekThing(), "peek");

        await ProcessBatchAsync();

        Assert.Equal([1], _sink.AttemptsSeenDuringExecution);
    }

    [Fact]
    public async Task A_command_interrupted_on_its_last_attempt_is_not_run_again_but_its_notice_is_still_sent()
    {
        // The process died inside the handler on the last attempt. Running it again after every restart
        // is how one poison command keeps taking the process down; ending it Failed without the notice is
        // how whoever waits on it waits for ever. Neither.
        await EnqueueAsync(new FailThing(), "interrupted");
        await StrandOnLastAttemptAsync();
        await RecoverAsync();
        Assert.Equal(CommandState.Queued, (await SingleCommandAsync()).State);

        await ProcessBatchAsync();

        var command = await SingleCommandAsync();
        Assert.Equal(CommandState.Failed, command.State);
        Assert.Equal(0, _sink.FailThingRuns);
        Assert.Equal(1, _sink.ExhaustedNotices);
    }

    [Fact]
    public async Task A_command_that_spends_its_last_attempt_tells_whoever_is_waiting_on_it()
    {
        await EnqueueAsync(new FailThing(), "last-attempt");
        await SpendAllButOneAttemptAsync();

        await ProcessBatchAsync();

        // Without the notice a module parked on this command's outcome would wait for ever: the Failed
        // row is read by an operator, not by the aggregate that needed the work done.
        var command = await SingleCommandAsync();
        Assert.Equal(CommandState.Failed, command.State);
        Assert.Equal(1, _sink.ExhaustedNotices);
    }

    [Fact]
    public async Task A_command_whose_exhaustion_could_not_be_reported_is_retried_rather_than_dropped()
    {
        _sink.FailExhaustedNotice = true;
        await EnqueueAsync(new FailThing(), "unreported");
        await SpendAllButOneAttemptAsync();

        await ProcessBatchAsync();

        // Ending Failed with the waiting side never told is the outcome this exists to prevent.
        var command = await SingleCommandAsync();
        Assert.Equal(CommandState.Queued, command.State);
        Assert.NotNull(command.RunAfter);

        // Retried for the notice alone: the handler already had its last attempt.
        _sink.FailExhaustedNotice = false;
        await MakeDueAsync();
        await ProcessBatchAsync();

        Assert.Equal(CommandState.Failed, (await SingleCommandAsync()).State);
        Assert.Equal(1, _sink.FailThingRuns);
        Assert.Equal(1, _sink.ExhaustedNotices);
    }

    [Fact]
    public async Task A_notice_that_can_never_be_delivered_does_not_keep_the_command_cycling_for_ever()
    {
        _sink.FailExhaustedNotice = true;
        await EnqueueAsync(new FailThing(), "never-told");
        await using (var scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<OperationsDbContext>().Database.ExecuteSqlAsync(
                $"UPDATE operations.command SET attempts = max_attempts + {CommandProcessor.MaxNoticeAttempts} - 1");
        }

        await ProcessBatchAsync();

        var command = await SingleCommandAsync();
        Assert.Equal(CommandState.Failed, command.State);
        Assert.Contains("could not be told", command.Error, StringComparison.Ordinal);
        Assert.Equal(0, _sink.FailThingRuns);
    }

    /// <summary>Leaves every command Running on its last attempt, as a crash inside the handler would.</summary>
    private async Task StrandOnLastAttemptAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<OperationsDbContext>().Database.ExecuteSqlAsync(
            $"UPDATE operations.command SET state = 'Running', attempts = max_attempts");
    }

    private async Task RecoverAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<CommandProcessor>().RecoverAsync();
    }

    private async Task MakeDueAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<OperationsDbContext>().Database.ExecuteSqlAsync(
            $"UPDATE operations.command SET run_after = NULL");
    }

    private async Task SpendAllButOneAttemptAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<OperationsDbContext>().Database.ExecuteSqlAsync(
            $"UPDATE operations.command SET attempts = max_attempts - 1");
    }

    private async Task<List<QueuedCommand>> AllCommandsAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
        return await dbContext.Commands.AsNoTracking().ToListAsync();
    }

    private async Task EnqueueAsync(ICommand command, string idempotencyKey)
    {
        await using var scope = _provider.CreateAsyncScope();
        var queue = scope.ServiceProvider.GetRequiredService<ICommandQueue>();
        await queue.EnqueueAsync(command, idempotencyKey);
    }

    private async Task<int> ProcessBatchAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var processor = scope.ServiceProvider.GetRequiredService<CommandProcessor>();
        return await processor.ProcessBatchAsync();
    }

    private async Task<QueuedCommand> SingleCommandAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
        return await dbContext.Commands.AsNoTracking().SingleAsync();
    }

    private sealed record DoThing(Guid ThingId) : ICommand;

    private sealed record FailThing : ICommand;

    private sealed record TimeOutThing : ICommand;

    private sealed record PeekThing : ICommand;

    private sealed class CommandSink
    {
        public ConcurrentBag<Guid> Handled { get; } = [];

        public ConcurrentQueue<int> AttemptsSeenDuringExecution { get; } = [];

        public int ExhaustedNotices;

        public int FailThingRuns;

        public bool FailExhaustedNotice { get; set; }
    }

    private sealed class DoThingHandler(CommandSink sink) : ICommandHandler<DoThing>
    {
        public Task<Result> HandleAsync(DoThing command, CancellationToken cancellationToken = default)
        {
            sink.Handled.Add(command.ThingId);
            return Task.FromResult(Result.Success());
        }
    }

    private sealed class TimeOutThingHandler : ICommandHandler<TimeOutThing>
    {
        // Async, as every real handler is: the exception arrives through the awaited task, not wrapped by
        // the dispatcher's reflective call.
        public async Task<Result> HandleAsync(TimeOutThing command, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.");
        }
    }

    /// <summary>Reads its own row mid-execution, from outside the processor, as a crash would leave it.</summary>
    private sealed class PeekThingHandler(CommandSink sink, IServiceScopeFactory scopes) : ICommandHandler<PeekThing>
    {
        public async Task<Result> HandleAsync(PeekThing command, CancellationToken cancellationToken = default)
        {
            await using var scope = scopes.CreateAsyncScope();
            var row = await scope.ServiceProvider.GetRequiredService<OperationsDbContext>()
                .Commands.AsNoTracking().SingleAsync(cancellationToken);
            sink.AttemptsSeenDuringExecution.Enqueue(row.Attempts);
            return Result.Success();
        }
    }

    private sealed class FailThingExhaustedHandler(CommandSink sink) : ICommandExhaustedHandler<FailThing>
    {
        public Task HandleExhaustedAsync(FailThing command, CancellationToken cancellationToken = default)
        {
            if (sink.FailExhaustedNotice)
            {
                throw new InvalidOperationException("the notice could not be recorded");
            }

            Interlocked.Increment(ref sink.ExhaustedNotices);
            return Task.CompletedTask;
        }
    }

    private sealed class FailThingHandler(CommandSink sink) : ICommandHandler<FailThing>
    {
        public Task<Result> HandleAsync(FailThing command, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref sink.FailThingRuns);
            return Task.FromResult(Result.Failure(new Error("test.boom", "boom")));
        }
    }
}
