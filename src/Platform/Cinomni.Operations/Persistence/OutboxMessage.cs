namespace Cinomni.Operations.Persistence;

/// <summary>
/// A domain event awaiting publication (transactional outbox, at-least-once).
/// It is written in the same transaction as the state change that produced it; the relay
/// later publishes it to the in-process handlers and marks it published.
/// </summary>
public sealed class OutboxMessage
{
    public Guid Id { get; init; }

    /// <summary>Stable registered name of the event type (not the .NET type name — rename-safe).</summary>
    public required string EventType { get; init; }

    /// <summary>JSON payload of the event.</summary>
    public required string Payload { get; init; }

    /// <summary>Semantic idempotency key so consumers can deduplicate.</summary>
    public required string IdempotencyKey { get; init; }

    public DateTimeOffset OccurredAt { get; init; }

    public bool Published { get; set; }

    public DateTimeOffset? PublishedAt { get; set; }

    /// <summary>
    /// W3C <c>traceparent</c> of the execution that published this event, so the relay can dispatch it
    /// on the same trace instead of starting an unrelated one. Null when nothing was tracing — which is
    /// the default build — and never required for delivery.
    /// <para>
    /// It is metadata, not payload: it is deliberately a column rather than a field inside
    /// <see cref="Payload"/>, so the wire contract consumers deserialize is unchanged and a message
    /// written before this column existed relays exactly as it always did.
    /// </para>
    /// </summary>
    public string? TraceParent { get; set; }

    /// <summary>W3C <c>tracestate</c> alongside <see cref="TraceParent"/>. Usually null.</summary>
    public string? TraceState { get; set; }

    /// <summary>How many deliveries of this message a handler has failed.</summary>
    public int Attempts { get; set; }

    /// <summary>Not offered to the relay before this instant: the backoff after a failed delivery.</summary>
    public DateTimeOffset? NextAttemptAt { get; set; }

    /// <summary>The exception type of the last failed delivery — a type name, never its message.</summary>
    public string? LastError { get; set; }

    /// <summary>
    /// Set once the message has failed <c>OutboxRelay.MaxAttempts</c> deliveries. It is no longer
    /// offered, so it cannot hold back the messages behind it; it is kept, unpublished, for an operator.
    /// </summary>
    public DateTimeOffset? DeadLetteredAt { get; set; }
}
