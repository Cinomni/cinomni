namespace Cinomni.Operations.Persistence;

/// <summary>Lifecycle states of a queued command.</summary>
public static class CommandState
{
    public const string Queued = "Queued";
    public const string Running = "Running";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
}

/// <summary>
/// A unit of recoverable background work persisted in the command queue. Workers claim it
/// with <c>FOR UPDATE SKIP LOCKED</c>, so concurrency needs no global lock; deduplicated on
/// <see cref="IdempotencyKey"/> at enqueue time.
/// </summary>
public sealed class QueuedCommand
{
    public Guid Id { get; init; }

    /// <summary>Stable registered name of the command type.</summary>
    public required string CommandType { get; init; }

    /// <summary>JSON payload of the command.</summary>
    public required string Payload { get; init; }

    /// <summary>Deduplication key: enqueuing the same key twice is a no-op.</summary>
    public required string IdempotencyKey { get; init; }

    public required string State { get; set; }

    public int Attempts { get; set; }

    public int MaxAttempts { get; init; }

    public DateTimeOffset QueuedAt { get; init; }

    /// <summary>When set, the command must not run before this instant (retry backoff).</summary>
    public DateTimeOffset? RunAfter { get; set; }

    public DateTimeOffset? LastAttemptAt { get; set; }

    public string? Error { get; set; }

    /// <summary>
    /// W3C <c>traceparent</c> of the execution that enqueued this command, so the worker executes it on
    /// the same trace. This is the second of the two asynchronous hops a cross-module flow makes, and
    /// without it a trace ends at the enqueue. Null when nothing was tracing; never required to run.
    /// <para>
    /// Metadata rather than payload, so the command's wire contract is unchanged and a row written
    /// before this column existed executes exactly as it always did.
    /// </para>
    /// </summary>
    public string? TraceParent { get; set; }

    /// <summary>W3C <c>tracestate</c> alongside <see cref="TraceParent"/>. Usually null.</summary>
    public string? TraceState { get; set; }
}
