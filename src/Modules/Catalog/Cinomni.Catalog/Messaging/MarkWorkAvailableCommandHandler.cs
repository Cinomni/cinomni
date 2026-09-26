using Cinomni.Catalog.Contracts;
using Cinomni.Kernel.Messaging;
using Cinomni.Kernel.Results;

namespace Cinomni.Catalog.Messaging;

/// <summary>Marks a work available on a completed import (⇠ Import's MediaAvailable).</summary>
public sealed class MarkWorkAvailableCommandHandler(ICatalogCommands commands)
    : ICommandHandler<MarkWorkAvailableCommand>
{
    public async Task<Result> HandleAsync(MarkWorkAvailableCommand command, CancellationToken cancellationToken = default)
    {
        await commands.MarkWorkAvailableAsync(command.WorkId, command.TargetId, cancellationToken);
        return Result.Success();
    }
}
