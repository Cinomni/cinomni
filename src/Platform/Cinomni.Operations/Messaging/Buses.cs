using Cinomni.Kernel.Messaging;
using Cinomni.Kernel.Results;

namespace Cinomni.Operations.Messaging;

/// <summary>
/// The platform command bus. Enqueues/dispatches commands in a recoverable and
/// idempotent way. Together with events, it is the only channel for
/// effectful communication between modules.
/// </summary>
public interface ICommandBus
{
    Task<Result> SendAsync(ICommand command, CancellationToken cancellationToken = default);

    Task<Result<TResult>> SendAsync<TResult>(ICommand<TResult> command, CancellationToken cancellationToken = default);
}

/// <summary>
/// The platform event bus. Publishes domain events through a transactional
/// <b>outbox</b> (at-least-once delivery); consumers deduplicate by
/// <see cref="IDomainEvent.IdempotencyKey"/>.
/// </summary>
public interface IEventBus
{
    Task PublishAsync(IDomainEvent domainEvent, CancellationToken cancellationToken = default);
}
