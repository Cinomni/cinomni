namespace Cinomni.Kernel.Messaging;

/// <summary>
/// A command: an intent with an effect, in the imperative (e.g. <c>AddDownload</c>).
/// Dispatched through the command bus and idempotent.
/// </summary>
public interface ICommand;

/// <summary>A command that produces a typed result.</summary>
/// <typeparam name="TResult">The result type.</typeparam>
public interface ICommand<TResult>;

/// <summary>
/// A domain event: something that has happened, in the past tense (e.g. <c>DownloadCompleted</c>).
/// Published through the outbox when relevant to recovery, carrying a semantic
/// idempotency key so consumers can deduplicate.
/// </summary>
public interface IDomainEvent
{
    /// <summary>Unique identifier of this event instance.</summary>
    Guid EventId { get; }

    /// <summary>Instant (UTC) at which the fact occurred.</summary>
    DateTimeOffset OccurredAt { get; }

    /// <summary>Semantic idempotency key (e.g. <c>downloadTaskId+state</c>).</summary>
    string IdempotencyKey { get; }
}
