using Cinomni.Kernel.Identifiers;
using Cinomni.Kernel.Messaging;
using Cinomni.Operations.Diagnostics;
using Cinomni.Operations.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cinomni.Operations.Messaging;

/// <summary>Enqueues commands for recoverable, idempotent background execution.</summary>
public interface ICommandQueue
{
    /// <summary>
    /// Enqueues a command. Enqueuing the same <paramref name="idempotencyKey"/> twice is a
    /// no-op, so callers can safely retry without producing duplicate work.
    /// <para>
    /// Returns <c>false</c> when the key was already spent and the command was therefore dropped. A
    /// caller whose key can legitimately recur must react to this rather than assume the work is
    /// under way.
    /// </para>
    /// <para>
    /// <b>A key is held by its row, and a terminal row is not kept for ever.</b> Retention removes a
    /// <c>Completed</c> command after <c>Retention:CompletedCommandRetention</c> and a <c>Failed</c>
    /// one after <c>Retention:FailedCommandRetention</c>, and the key is free again from that moment.
    /// Both windows are validated to exceed <c>Retention:OutboxRetention</c>, so a message that can
    /// still be published can never re-enqueue work whose dedup row has gone. Beyond that, a key must
    /// be either message-scoped (derived from the id of the event or command that produced it) or
    /// safely repeatable — never the only thing standing between a domain-identity key such as
    /// <c>create-intent:{TargetId}</c> and a side effect that must happen exactly once for all time.
    /// </para>
    /// </summary>
    Task<bool> EnqueueAsync(ICommand command, string idempotencyKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Enqueues a command that must not run before <paramref name="runAfter"/>. Same deduplication,
    /// same <c>false</c>-means-dropped contract and the same key-lifetime rule as
    /// <see cref="EnqueueAsync"/>; the worker's claim query already honours the instant.
    /// <para>
    /// This is how a producer paces work it knows will burst — one command worker dispatches its batch
    /// strictly sequentially, so a handler that sleeps to respect an external rate limit stalls every
    /// other module's commands behind it. Spreading the schedule keeps the limit without the stall.
    /// </para>
    /// </summary>
    Task<bool> EnqueueAtAsync(
        ICommand command,
        string idempotencyKey,
        DateTimeOffset runAfter,
        CancellationToken cancellationToken = default);
}

public sealed class CommandQueue(
    OperationsDbContext dbContext,
    IMessageTypeRegistry typeRegistry,
    IMessageSerializer serializer,
    ILogger<CommandQueue> logger)
    : ICommandQueue
{
    private const int DefaultMaxAttempts = 5;

    public Task<bool> EnqueueAsync(ICommand command, string idempotencyKey, CancellationToken cancellationToken = default) =>
        InsertAsync(command, idempotencyKey, runAfter: null, cancellationToken);

    public Task<bool> EnqueueAtAsync(
        ICommand command,
        string idempotencyKey,
        DateTimeOffset runAfter,
        CancellationToken cancellationToken = default) =>
        InsertAsync(command, idempotencyKey, runAfter, cancellationToken);

    private async Task<bool> InsertAsync(
        ICommand command,
        string idempotencyKey,
        DateTimeOffset? runAfter,
        CancellationToken cancellationToken)
    {
        var id = Uuid7.New();
        var commandType = typeRegistry.GetName(command.GetType());
        var payload = serializer.Serialize(command);
        var now = DateTimeOffset.UtcNow;

        // The enqueuing execution's trace, so the worker runs the handler on the same trace minutes or
        // a restart later. Metadata columns, never part of the payload the handler deserializes.
        var (traceParent, traceState) = MessageTracing.Capture();

        // ON CONFLICT gives atomic dedup on the unique idempotency key, avoiding a
        // check-then-insert race. Still fully parameterized — the trace context is two more parameters,
        // not string interpolation, even though it is machine-generated.
        var inserted = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO operations.command
                 (id, command_type, payload, idempotency_key, state, attempts, max_attempts, queued_at,
                  run_after, trace_parent, trace_state)
             VALUES
                 ({id}, {commandType}, {payload}::jsonb, {idempotencyKey}, 'Queued', 0, {DefaultMaxAttempts}, {now},
                  {runAfter}, {traceParent}, {traceState})
             ON CONFLICT (idempotency_key) DO NOTHING
             """,
            cancellationToken);

        if (inserted > 0)
        {
            return true;
        }

        // Never silent. A dropped command writes no row, publishes no event and fails nothing, so
        // without this line and its counter the only symptom of a key collision is work that never
        // happens.
        OperationsMetrics.RecordCommandDropped(commandType);
        logger.LogInformation(
            "Command {CommandType} was not enqueued: idempotency key {IdempotencyKey} is already spent.",
            commandType,
            idempotencyKey);
        return false;
    }
}
