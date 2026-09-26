using System.Diagnostics;
using Cinomni.Kernel.Diagnostics;
using Cinomni.Operations.Diagnostics;
using Cinomni.Operations.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cinomni.Operations.Messaging;

/// <summary>
/// Core of the command worker. Two phases so that a crash never loses or duplicates work:
/// phase 1 atomically claims a batch (<c>FOR UPDATE SKIP LOCKED</c>) and marks it Running;
/// phase 2 executes each outside the lock and records the outcome. A command left Running by
/// a crash is re-queued at startup by <see cref="RecoverAsync"/> — safe because handlers are
/// idempotent.
/// <para>
/// Every scheduling decision — which commands are due, and when a failed one may be retried — reads
/// <paramref name="timeProvider"/>. Production supplies <see cref="TimeProvider.System"/>; a test that
/// asserts a deferred command is *not* picked up early can supply a virtual clock and step it.
/// </para>
/// </summary>
public sealed class CommandProcessor(
    OperationsDbContext dbContext,
    ICommandDispatcher dispatcher,
    TimeProvider timeProvider)
{
    public const int BatchSize = 20;

    /// <summary>
    /// Extra claims a spent command gets for its exhausted notice alone, after which it ends Failed with
    /// the notice undelivered. Bounded, because a notice that can never succeed — a bug, a payload that no
    /// longer deserializes — must not keep a row cycling for ever.
    /// </summary>
    public const int MaxNoticeAttempts = 5;
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Requeues commands stranded in <c>Running</c> by a crash or a failed batch. Every one goes back to
    /// Queued, including one interrupted on its last attempt: attempts are counted at the claim, so its
    /// next claim finds them spent and settles it without running the handler again — which is what
    /// stops a handler that takes the process down from doing so after every restart — while still
    /// delivering the exhausted notice whoever is waiting on it is owed.
    /// </summary>
    public Task<int> RecoverAsync(CancellationToken cancellationToken = default) =>
        dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE operations.command SET state = 'Queued' WHERE state = 'Running'",
            cancellationToken);

    /// <summary>Processes one batch. Returns the number of commands attempted.</summary>
    public async Task<int> ProcessBatchAsync(CancellationToken cancellationToken = default)
    {
        var claimedAt = timeProvider.GetUtcNow();

        // Phase 1: claim a batch and mark it Running, in one short transaction.
        List<QueuedCommand> batch;
        await using (var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken))
        {
            batch = await dbContext.Commands
                .FromSql(
                    $"""
                     SELECT * FROM operations.command
                     WHERE state = 'Queued' AND (run_after IS NULL OR run_after <= {claimedAt})
                     ORDER BY queued_at
                     LIMIT {BatchSize}
                     FOR UPDATE SKIP LOCKED
                     """)
                .ToListAsync(cancellationToken);

            if (batch.Count == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return 0;
            }

            foreach (var command in batch)
            {
                command.State = CommandState.Running;
                command.LastAttemptAt = claimedAt;
                // Counted here, committed with the claim: an attempt that never reports back — the
                // process died inside the handler — is still an attempt.
                command.Attempts++;
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        // Phase 2: execute each. The lock is released; a crash here leaves the command
        // Running for RecoverAsync to requeue.
        foreach (var command in batch)
        {
            // The second hop that would otherwise break the trace: this row may have been enqueued
            // minutes ago, by another request, or before a restart. Re-parenting to the stored context
            // puts the handler back on the trace that caused the work.
            using var activity = MessageTracing.StartConsumer(
                $"command {command.CommandType}", command.TraceParent, command.TraceState);

            activity?.SetTag(CinomniTelemetry.Tags.Module, CinomniTelemetry.Modules.Operations);
            activity?.SetTag(CinomniTelemetry.Tags.CommandName, command.CommandType);

            // The histogram measures how long the handler really took, so it stays on the stopwatch
            // even when the scheduling clock is virtual.
            var startedAt = Stopwatch.GetTimestamp();

            // Claimed past its last attempt: the run that spent it never reported back (the process
            // died, or the batch failed before the outcome was saved). The handler is not run again;
            // only the notice is still owed.
            var spent = command.Attempts > command.MaxAttempts;
            var (succeeded, error) = spent
                ? (false, command.Error ?? "Interrupted on its last attempt; not run again.")
                : await TryDispatchAsync(command, cancellationToken);
            var elapsed = Stopwatch.GetElapsedTime(startedAt);
            var now = timeProvider.GetUtcNow();

            string outcome;
            if (succeeded)
            {
                command.State = CommandState.Completed;
                command.Error = null;
                outcome = CinomniTelemetry.Outcomes.Completed;
            }
            else if (command.Attempts >= command.MaxAttempts
                && await TryNotifyExhaustedAsync(command, cancellationToken))
            {
                command.State = CommandState.Failed;
                command.Error = error;
                outcome = CinomniTelemetry.Outcomes.Failed;
            }
            else if (command.Attempts >= command.MaxAttempts + MaxNoticeAttempts)
            {
                command.State = CommandState.Failed;
                command.Error = "Attempts spent, and whoever was waiting on this command could not be told.";
                outcome = CinomniTelemetry.Outcomes.Failed;
            }
            else
            {
                command.State = CommandState.Queued;
                command.RunAfter = now + Backoff(command.Attempts);
                command.Error = error;
                outcome = CinomniTelemetry.Outcomes.Retried;
            }

            // The status word travels; the failure text does not. `error` is an exception message, so it
            // can quote the input that produced it — a provider URL, a path, a torrent name. It belongs
            // in the command row, which only an administrator reads, not in an exported span.
            activity?.SetTag(CinomniTelemetry.Tags.Outcome, outcome);
            if (!succeeded)
            {
                activity?.SetStatus(ActivityStatusCode.Error);
            }

            OperationsMetrics.RecordCommandExecuted(command.CommandType, outcome, elapsed);

            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return batch.Count;
    }

    private async Task<(bool Succeeded, string? Error)> TryDispatchAsync(
        QueuedCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await dispatcher.DispatchAsync(command, cancellationToken);
            return result.IsSuccess ? (true, null) : (false, result.Error.Message);
        }
        // Keyed on the caller's token, not on the exception type. An HttpClient timeout is a
        // TaskCanceledException too, and letting it through escaped the batch before the outcome was
        // saved: this command and the rest of its batch stayed Running until the next restart. Only
        // the host shutting down is allowed past, and RecoverAsync requeues what that leaves behind.
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Tells whoever is waiting on this command that it will not run again. False when that could not
    /// be done: the command is then retried like any other failure rather than ending Failed with the
    /// waiting side never told — and told again, idempotently, when it is spent once more.
    /// </summary>
    private async Task<bool> TryNotifyExhaustedAsync(QueuedCommand command, CancellationToken cancellationToken)
    {
        try
        {
            await dispatcher.NotifyExhaustedAsync(command, cancellationToken);
            return true;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private static TimeSpan Backoff(int attempts)
    {
        var seconds = Math.Min(MaxBackoff.TotalSeconds, Math.Pow(2, attempts));
        return TimeSpan.FromSeconds(seconds);
    }
}
