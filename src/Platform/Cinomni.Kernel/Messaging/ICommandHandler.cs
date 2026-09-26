using Cinomni.Kernel.Results;

namespace Cinomni.Kernel.Messaging;

/// <summary>
/// Handles a queued command. Commands are the unit of recoverable, idempotent background
/// work: a handler must tolerate being run again for the same command.
/// </summary>
/// <typeparam name="TCommand">The command type this handler executes.</typeparam>
public interface ICommandHandler<in TCommand>
    where TCommand : ICommand
{
    Task<Result> HandleAsync(TCommand command, CancellationToken cancellationToken = default);
}

/// <summary>
/// Told once a command has spent its last attempt and will not run again. Optional: a command with
/// no such handler simply ends Failed, as before.
/// <para>
/// It exists for work somebody else is waiting on. A command that fails for good records that in its
/// own row, which only an operator reads; a module whose aggregate is parked on the outcome would
/// otherwise wait for ever. The handler is where the owning module says so, typically by publishing
/// an event. It must be idempotent: when it fails, the command is retried and it is told again.
/// </para>
/// </summary>
/// <typeparam name="TCommand">The command type whose exhaustion this handler reports.</typeparam>
public interface ICommandExhaustedHandler<in TCommand>
    where TCommand : ICommand
{
    Task HandleExhaustedAsync(TCommand command, CancellationToken cancellationToken = default);
}
