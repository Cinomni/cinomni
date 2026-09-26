using Cinomni.Import.Contracts;
using Cinomni.Kernel.Messaging;
using Cinomni.Kernel.Results;

namespace Cinomni.Import.Messaging;

/// <summary>Runs <see cref="ProcessCompletedDownloadCommand"/> through the import processor (⇠ DownloadCompleted).</summary>
public sealed class ProcessCompletedDownloadCommandHandler(IImportProcessor processor)
    : ICommandHandler<ProcessCompletedDownloadCommand>
{
    public async Task<Result> HandleAsync(ProcessCompletedDownloadCommand command, CancellationToken cancellationToken = default)
    {
        await processor.ProcessCompletedDownloadAsync(
            command.DownloadTaskId,
            command.IntentId,
            command.AttemptId,
            command.WorkId,
            command.TargetId,
            command.ContentPath,
            command.UnitIds,
            cancellationToken);
        return Result.Success();
    }
}
