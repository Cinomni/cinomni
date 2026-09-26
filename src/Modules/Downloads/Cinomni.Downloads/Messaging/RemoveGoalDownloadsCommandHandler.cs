using Cinomni.Downloads.Application;
using Cinomni.Kernel.Messaging;
using Cinomni.Kernel.Results;

namespace Cinomni.Downloads.Messaging;

/// <summary>Drops every torrent a cancelled goal claimed (⇠ Acquisition's AcquisitionCancelled).</summary>
public sealed class RemoveGoalDownloadsCommandHandler(DownloadService service)
    : ICommandHandler<RemoveGoalDownloadsCommand>
{
    public async Task<Result> HandleAsync(RemoveGoalDownloadsCommand command, CancellationToken cancellationToken = default)
    {
        await service.RemoveForGoalAsync(command.IntentId, command.DeleteFiles, cancellationToken);
        return Result.Success();
    }
}
