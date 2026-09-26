namespace Cinomni.Kernel.Messaging;

/// <summary>
/// Handles a domain event delivered through the outbox. A module registers one handler
/// per event it reacts to; handlers must be idempotent (the outbox is at-least-once).
/// </summary>
/// <typeparam name="TEvent">The domain event type this handler reacts to.</typeparam>
public interface IEventHandler<in TEvent>
    where TEvent : IDomainEvent
{
    Task HandleAsync(TEvent domainEvent, CancellationToken cancellationToken = default);
}
