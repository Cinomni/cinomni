using Cinomni.Downloads.Application;
using Cinomni.Downloads.Persistence;
using Cinomni.Kernel.Messaging;
using Cinomni.Kernel.Results;
using Cinomni.Operations.Messaging;

namespace Cinomni.Downloads.Messaging;

/// <summary>
/// Runs <see cref="AddDownloadCommand"/> through the download service (⇠ DownloadQueued), and says so
/// when it never can.
/// </summary>
public sealed class AddDownloadCommandHandler(DownloadService service, ICommandQueue commandQueue, TimeProvider clock)
    : ICommandHandler<AddDownloadCommand>, ICommandExhaustedHandler<AddDownloadCommand>
{
    /// <summary>
    /// How long a release handed off during an egress hold waits before it is tried again. The hold is
    /// the tunnel's, not the release's: it is waited out without spending the command's attempts, which
    /// a hold of a few minutes would otherwise exhaust and report as a release that failed.
    /// </summary>
    internal static readonly TimeSpan HeldRetryDelay = TimeSpan.FromMinutes(1);

    public async Task<Result> HandleAsync(AddDownloadCommand command, CancellationToken cancellationToken = default)
    {
        try
        {
            await service.AddDownloadAsync(
                command.AttemptId,
                command.IntentId,
                command.WorkId,
                command.TargetId,
                command.ReleaseGuid,
                command.DownloadUrl,
                command.UnitIds,
                cancellationToken);
        }
        catch (NetworkHoldException)
        {
            // A fresh command rather than a retry of this one, keyed by the minute it is due in: a
            // redelivery within the same minute collapses into it, and the chain lasts as long as the
            // hold does. The add itself is idempotent per attempt, so an overlap costs nothing.
            // The queue's own clock, so the delay is measured the way the worker will measure it.
            var runAfter = clock.GetUtcNow() + HeldRetryDelay;
            await commandQueue.EnqueueAtAsync(
                command,
                $"add-download:{command.AttemptId}:held:{runAfter.ToUnixTimeSeconds() / 60}",
                runAfter,
                cancellationToken);
        }

        return Result.Success();
    }

    public Task HandleExhaustedAsync(AddDownloadCommand command, CancellationToken cancellationToken = default) =>
        service.ReportNotStartedAsync(command.AttemptId, command.IntentId, cancellationToken);
}
