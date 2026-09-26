using System.Diagnostics;
using Cinomni.Kernel.Diagnostics;
using Cinomni.Operations.Diagnostics;
using Cinomni.Operations.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cinomni.Operations.Messaging;

/// <summary>
/// Core of the outbox relay: claims a batch of deliverable messages with
/// <c>FOR UPDATE SKIP LOCKED</c> (so multiple workers never grab the same row), dispatches
/// each to its handlers, and marks them published — all in one transaction. Exposed as a
/// callable unit so tests can drive it deterministically without the timing of a loop.
/// <para>
/// Each message is dispatched under its own savepoint. A handler that fails rolls back only what
/// that message did, the message is retried later with a growing backoff, and after
/// <see cref="MaxAttempts"/> failures it is set aside as a dead letter. Before, one message whose
/// handler always threw rolled back its whole batch on every tick, and — being the oldest — blocked
/// every event behind it indefinitely.
/// </para>
/// </summary>
public sealed class OutboxRelay(OperationsDbContext dbContext, IEventDispatcher dispatcher, ILogger<OutboxRelay>? logger = null)
{
    public const int BatchSize = 50;

    /// <summary>Failed deliveries after which a message is dead-lettered instead of retried.</summary>
    public const int MaxAttempts = 10;

    private const string Savepoint = "outbox_message";

    private static readonly TimeSpan FirstBackoff = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(1);

    /// <summary>Processes one batch. Returns the number of messages published.</summary>
    public async Task<int> ProcessBatchAsync(CancellationToken cancellationToken = default)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var batch = await dbContext.Outbox
            .FromSql(
                $"""
                 SELECT * FROM operations.outbox
                 WHERE published = false
                   AND dead_lettered_at IS NULL
                   AND (next_attempt_at IS NULL OR next_attempt_at <= {now})
                 ORDER BY occurred_at
                 LIMIT {BatchSize}
                 FOR UPDATE SKIP LOCKED
                 """)
            .ToListAsync(cancellationToken);

        if (batch.Count == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return 0;
        }

        var claimed = batch.ToHashSet();
        var published = 0;
        foreach (var message in batch)
        {
            // The hop that would otherwise break the trace. Re-parenting the dispatch to the context
            // the publisher stored means the handler — and every command it enqueues, and every event
            // those produce — stays on the trace the original request started.
            using var activity = MessageTracing.StartConsumer(
                $"outbox.relay {message.EventType}", message.TraceParent, message.TraceState);

            activity?.SetTag(CinomniTelemetry.Tags.Module, CinomniTelemetry.Modules.Operations);
            activity?.SetTag(CinomniTelemetry.Tags.EventName, message.EventType);

            var startedAt = Stopwatch.GetTimestamp();
            await transaction.CreateSavepointAsync(Savepoint, cancellationToken);
            DateTimeOffset publishedAt;
            try
            {
                await dispatcher.DispatchAsync(message, cancellationToken);

                publishedAt = DateTimeOffset.UtcNow;
                message.Published = true;
                message.PublishedAt = publishedAt;
                // Written before the next savepoint opens, so a later message's rollback cannot take
                // it — and inside this try, because a handler that swallowed a database error leaves
                // the transaction unable to write, which is this message's failure, not the batch's.
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // Only the host shutting down escapes: a handler's own timeout is a failed delivery like
                // any other, and letting it through would block the queue behind this message again.
                // Only this message's work is undone; what the batch already delivered stands.
                await transaction.RollbackToSavepointAsync(Savepoint, cancellationToken);
                ForgetWhatTheHandlerLeftTracked(claimed);
                message.Published = false;
                message.PublishedAt = null;
                RecordFailedDelivery(message, ex, DateTimeOffset.UtcNow);
                activity?.SetStatus(ActivityStatusCode.Error);
                OperationsMetrics.RecordOutboxBatchFailure();
                await dbContext.SaveChangesAsync(cancellationToken);
                continue;
            }

            published++;

            OperationsMetrics.RecordOutboxPublished(
                message.EventType,
                Stopwatch.GetElapsedTime(startedAt),
                publishedAt - message.OccurredAt);
        }

        await transaction.CommitAsync(cancellationToken);
        return published;
    }

    /// <summary>
    /// A handler that failed may have left entities tracked: added but never saved, which the next save
    /// would write after all, or saved and then rolled back, which the tracker still believes exist.
    /// Everything tracked that is not one of this batch's messages is let go, whatever its state.
    /// </summary>
    private void ForgetWhatTheHandlerLeftTracked(IReadOnlySet<OutboxMessage> claimed)
    {
        foreach (var entry in dbContext.ChangeTracker.Entries().ToList())
        {
            if (entry.Entity is OutboxMessage message && claimed.Contains(message))
            {
                continue;
            }

            entry.State = EntityState.Detached;
        }
    }

    private void RecordFailedDelivery(OutboxMessage message, Exception failure, DateTimeOffset now)
    {
        message.Attempts++;
        message.LastError = Truncate(failure.GetType().Name, 200);
        if (message.Attempts >= MaxAttempts)
        {
            message.DeadLetteredAt = now;
            message.NextAttemptAt = null;
            OperationsMetrics.RecordOutboxDeadLettered(message.EventType);
            // The type and id are what an operator needs to find it; the exception carries its detail.
            logger?.LogError(
                failure,
                "Outbox message {MessageId} ({EventType}) failed {Attempts} deliveries and was set aside; the events behind it go on.",
                message.Id,
                message.EventType,
                message.Attempts);
            return;
        }

        var doublings = Math.Clamp(message.Attempts - 1, 0, 16);
        var backoff = TimeSpan.FromTicks(Math.Min(FirstBackoff.Ticks << doublings, MaxBackoff.Ticks));
        message.NextAttemptAt = now + backoff;
        logger?.LogWarning(
            failure,
            "Outbox message {MessageId} ({EventType}) failed delivery {Attempts} of {MaxAttempts}; retrying after {Backoff}.",
            message.Id,
            message.EventType,
            message.Attempts,
            MaxAttempts,
            backoff);
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
