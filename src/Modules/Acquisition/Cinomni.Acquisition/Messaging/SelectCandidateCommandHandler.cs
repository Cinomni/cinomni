using Cinomni.Acquisition.Contracts;
using Cinomni.Kernel.Messaging;
using Cinomni.Kernel.Results;

namespace Cinomni.Acquisition.Messaging;

/// <summary>Runs <see cref="SelectCandidateCommand"/> through the module's command service.</summary>
public sealed class SelectCandidateCommandHandler(IAcquisitionCommands commands)
    : ICommandHandler<SelectCandidateCommand>
{
    public async Task<Result> HandleAsync(
        SelectCandidateCommand command,
        CancellationToken cancellationToken = default)
    {
        await commands.SelectCandidateAsync(
            command.EvaluationId,
            command.TargetId,
            command.ReleaseGuid,
            command.DownloadUrl,
            command.UnitIds,
            command.Release,
            cancellationToken);
        return Result.Success();
    }
}
