using Cinomni.Kernel.Identifiers;
using Cinomni.Kernel.Messaging;
using Cinomni.Operations.Diagnostics;
using Cinomni.Operations.Persistence;

namespace Cinomni.Operations.Messaging;

/// <summary>
/// <see cref="IEventBus"/> backed by the transactional outbox: publishing enqueues the
/// event as an <see cref="OutboxMessage"/>. The actual delivery to handlers is done later
/// by the <see cref="OutboxRelay"/> (at-least-once).
/// </summary>
/// <remarks>
/// M0: this enqueues and saves in its own unit of work. Atomicity with a module's own
/// state change (writing the event in the same transaction as the aggregate) is added when
/// the first stateful module lands, by sharing the DbContext transaction.
/// </remarks>
public sealed class OutboxEventBus(
    OperationsDbContext dbContext,
    IMessageTypeRegistry typeRegistry,
    IMessageSerializer serializer)
    : IEventBus
{
    public async Task PublishAsync(IDomainEvent domainEvent, CancellationToken cancellationToken = default)
    {
        // Captured here, in the publisher's own execution context, and stored on the row inside the
        // same transaction as the message. Read again by the relay, which is what keeps an acquisition
        // one trace rather than a new one per hop.
        var (traceParent, traceState) = MessageTracing.Capture();

        dbContext.Outbox.Add(new OutboxMessage
        {
            Id = Uuid7.New(),
            EventType = typeRegistry.GetName(domainEvent.GetType()),
            Payload = serializer.Serialize(domainEvent),
            IdempotencyKey = domainEvent.IdempotencyKey,
            OccurredAt = domainEvent.OccurredAt,
            Published = false,
            TraceParent = traceParent,
            TraceState = traceState,
        });

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
