using System.Collections.Concurrent;
using System.Reflection;
using Cinomni.Kernel.Messaging;
using Cinomni.Operations.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Operations.Messaging;

/// <summary>Delivers a stored outbox message to every registered handler for its event type.</summary>
public interface IEventDispatcher
{
    Task DispatchAsync(OutboxMessage message, CancellationToken cancellationToken = default);
}

public sealed class EventDispatcher(
    IServiceProvider services,
    IMessageTypeRegistry typeRegistry,
    IMessageSerializer serializer)
    : IEventDispatcher
{
    // Cache the closed handler interface + HandleAsync method per event type.
    private static readonly ConcurrentDictionary<Type, (Type HandlerType, MethodInfo Handle)> Cache = new();

    public async Task DispatchAsync(OutboxMessage message, CancellationToken cancellationToken = default)
    {
        var eventType = typeRegistry.Resolve(message.EventType);
        var domainEvent = (IDomainEvent)serializer.Deserialize(message.Payload, eventType);

        var (handlerType, handle) = Cache.GetOrAdd(eventType, static t =>
        {
            var handlerType = typeof(IEventHandler<>).MakeGenericType(t);
            var handle = handlerType.GetMethod(nameof(IEventHandler<IDomainEvent>.HandleAsync))!;
            return (handlerType, handle);
        });

        foreach (var handler in services.GetServices(handlerType))
        {
            if (handler is null)
            {
                continue;
            }

            await (Task)handle.Invoke(handler, [domainEvent, cancellationToken])!;
        }
    }
}
