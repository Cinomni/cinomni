using Cinomni.Acquisition.Contracts;
using Cinomni.Kernel.Messaging;
using Cinomni.Kernel.Results;

namespace Cinomni.Acquisition.Messaging;

/// <summary>Runs <see cref="CreateAcquisitionIntentCommand"/> through the module's command service.</summary>
public sealed class CreateAcquisitionIntentCommandHandler(IAcquisitionCommands commands)
    : ICommandHandler<CreateAcquisitionIntentCommand>
{
    public async Task<Result> HandleAsync(
        CreateAcquisitionIntentCommand command,
        CancellationToken cancellationToken = default)
    {
        await commands.CreateIntentAsync(
            command.TargetId, command.WorkId, command.Mode, command.UnitId, cancellationToken);
        return Result.Success();
    }
}

/// <summary>Meets every goal whose unit landed (⇠ Import's ImportCompleted).</summary>
public sealed class MarkUnitsSatisfiedCommandHandler(IAcquisitionCommands commands)
    : ICommandHandler<MarkUnitsSatisfiedCommand>
{
    public async Task<Result> HandleAsync(
        MarkUnitsSatisfiedCommand command,
        CancellationToken cancellationToken = default)
    {
        await commands.MarkUnitsSatisfiedAsync(command.UnitIds, command.AssetId, cancellationToken);
        return Result.Success();
    }
}
