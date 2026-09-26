using System.Collections.Concurrent;
using System.Reflection;
using Cinomni.Kernel.Messaging;
using Cinomni.Kernel.Results;
using Cinomni.Operations.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Operations.Messaging;

/// <summary>Executes a stored command by invoking its single registered handler.</summary>
public interface ICommandDispatcher
{
    Task<Result> DispatchAsync(QueuedCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tells the command's <see cref="ICommandExhaustedHandler{TCommand}"/>, if it has one, that its
    /// attempts are spent. A command without one is not an error: nothing is waiting on it.
    /// </summary>
    Task NotifyExhaustedAsync(QueuedCommand command, CancellationToken cancellationToken = default);
}

public sealed class CommandDispatcher(
    IServiceProvider services,
    IMessageTypeRegistry typeRegistry,
    IMessageSerializer serializer)
    : ICommandDispatcher
{
    private static readonly ConcurrentDictionary<Type, (Type HandlerType, MethodInfo Handle)> Cache = new();

    private static readonly ConcurrentDictionary<Type, (Type HandlerType, MethodInfo Handle)> ExhaustedCache = new();

    public async Task<Result> DispatchAsync(QueuedCommand command, CancellationToken cancellationToken = default)
    {
        var commandType = typeRegistry.Resolve(command.CommandType);
        var instance = serializer.Deserialize(command.Payload, commandType);

        var (handlerType, handle) = Cache.GetOrAdd(commandType, static t =>
        {
            var handlerType = typeof(ICommandHandler<>).MakeGenericType(t);
            var handle = handlerType.GetMethod(nameof(ICommandHandler<ICommand>.HandleAsync))!;
            return (handlerType, handle);
        });

        // Exactly one handler per command (unlike events, which may have many).
        var handler = services.GetService(handlerType)
            ?? throw new InvalidOperationException($"No handler registered for command '{command.CommandType}'.");

        return await (Task<Result>)handle.Invoke(handler, [instance, cancellationToken])!;
    }

    public async Task NotifyExhaustedAsync(QueuedCommand command, CancellationToken cancellationToken = default)
    {
        var commandType = typeRegistry.Resolve(command.CommandType);
        var (handlerType, handle) = ExhaustedCache.GetOrAdd(commandType, static t =>
        {
            var handlerType = typeof(ICommandExhaustedHandler<>).MakeGenericType(t);
            var handle = handlerType.GetMethod(nameof(ICommandExhaustedHandler<ICommand>.HandleExhaustedAsync))!;
            return (handlerType, handle);
        });

        if (services.GetService(handlerType) is not { } handler)
        {
            return;
        }

        var instance = serializer.Deserialize(command.Payload, commandType);
        await (Task)handle.Invoke(handler, [instance, cancellationToken])!;
    }
}
