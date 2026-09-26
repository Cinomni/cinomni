using Cinomni.Acquisition.Contracts;
using Cinomni.Kernel.Messaging;
using Cinomni.Kernel.Results;

namespace Cinomni.Acquisition.Messaging;

/// <summary>Confirms an attempt began transferring (⇠ Downloads' DownloadStarted).</summary>
public sealed class MarkDownloadStartedCommandHandler(IAcquisitionCommands commands)
    : ICommandHandler<MarkDownloadStartedCommand>
{
    public async Task<Result> HandleAsync(MarkDownloadStartedCommand command, CancellationToken cancellationToken = default)
    {
        await commands.MarkDownloadStartedAsync(command.IntentId, cancellationToken);
        return Result.Success();
    }
}

/// <summary>Advances the goal to Importing on a completed download (⇠ Downloads' DownloadCompleted).</summary>
public sealed class MarkDownloadCompletedCommandHandler(IAcquisitionCommands commands)
    : ICommandHandler<MarkDownloadCompletedCommand>
{
    public async Task<Result> HandleAsync(MarkDownloadCompletedCommand command, CancellationToken cancellationToken = default)
    {
        await commands.MarkDownloadCompletedAsync(command.IntentId, cancellationToken);
        return Result.Success();
    }
}

/// <summary>Retries or exhausts the goal when its release never became a download (⇠ DownloadNotStarted).</summary>
public sealed class MarkDownloadNotStartedCommandHandler(IAcquisitionCommands commands)
    : ICommandHandler<MarkDownloadNotStartedCommand>
{
    public async Task<Result> HandleAsync(MarkDownloadNotStartedCommand command, CancellationToken cancellationToken = default)
    {
        await commands.MarkDownloadNotStartedAsync(command.IntentId, command.AttemptId, command.Reason, cancellationToken);
        return Result.Success();
    }
}

/// <summary>Retries or exhausts the goal on a failed download (⇠ Downloads' DownloadFailed).</summary>
public sealed class MarkDownloadFailedCommandHandler(IAcquisitionCommands commands)
    : ICommandHandler<MarkDownloadFailedCommand>
{
    public async Task<Result> HandleAsync(MarkDownloadFailedCommand command, CancellationToken cancellationToken = default)
    {
        await commands.MarkDownloadFailedAsync(command.IntentId, command.Reason, cancellationToken);
        return Result.Success();
    }
}
