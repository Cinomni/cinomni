using Cinomni.Acquisition.Contracts;
using Cinomni.Kernel.Messaging;
using Cinomni.Kernel.Results;

namespace Cinomni.Acquisition.Messaging;

/// <summary>Meets the goal on a completed import (⇠ Import's ImportCompleted).</summary>
public sealed class MarkImportedCommandHandler(IAcquisitionCommands commands)
    : ICommandHandler<MarkImportedCommand>
{
    public async Task<Result> HandleAsync(MarkImportedCommand command, CancellationToken cancellationToken = default)
    {
        await commands.MarkImportedAsync(command.IntentId, cancellationToken);
        return Result.Success();
    }
}

/// <summary>Retries or exhausts the goal on a failed import (⇠ Import's ImportFailed).</summary>
public sealed class MarkImportFailedCommandHandler(IAcquisitionCommands commands)
    : ICommandHandler<MarkImportFailedCommand>
{
    public async Task<Result> HandleAsync(MarkImportFailedCommand command, CancellationToken cancellationToken = default)
    {
        await commands.MarkImportFailedAsync(command.IntentId, command.Reason, cancellationToken);
        return Result.Success();
    }
}
