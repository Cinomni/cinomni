using Cinomni.Acquisition.Contracts;
using Cinomni.Kernel.Messaging;
using Cinomni.Kernel.Results;

namespace Cinomni.Acquisition.Messaging;

/// <summary>Cancels the goals of a removed work (⇠ Monitoring's MonitoringRemoved).</summary>
public sealed class CancelWorkGoalsCommandHandler(IAcquisitionCommands commands)
    : ICommandHandler<CancelWorkGoalsCommand>
{
    public async Task<Result> HandleAsync(CancelWorkGoalsCommand command, CancellationToken cancellationToken = default)
    {
        await commands.CancelWorkGoalsAsync(command.WorkId, command.DeleteFiles, cancellationToken);
        return Result.Success();
    }
}
