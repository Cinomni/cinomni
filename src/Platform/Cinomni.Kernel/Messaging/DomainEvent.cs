using Cinomni.Kernel.Identifiers;

namespace Cinomni.Kernel.Messaging;

/// <summary>
/// Convenience base for domain events: assigns a UUIDv7 <see cref="EventId"/> and an
/// <see cref="OccurredAt"/> timestamp. Subclasses only need to express their payload and
/// their <see cref="IdempotencyKey"/>.
/// </summary>
public abstract record DomainEvent : IDomainEvent
{
    public Guid EventId { get; init; } = Uuid7.New();

    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;

    public abstract string IdempotencyKey { get; }
}
