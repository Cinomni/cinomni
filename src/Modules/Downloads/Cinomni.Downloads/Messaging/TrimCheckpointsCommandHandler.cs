using Cinomni.Downloads.Application;
using Cinomni.Downloads.Contracts;
using Cinomni.Downloads.Persistence;
using Cinomni.Kernel.Messaging;
using Cinomni.Kernel.Results;
using Cinomni.Operations.Retention;
using Microsoft.Extensions.Logging;

namespace Cinomni.Downloads.Messaging;

/// <summary>
/// Releases the libtorrent resume blob of a finished download the engine stopped reporting longer ago
/// than the window. The task, its files, its units and its history all stay: they are the acquisition
/// audit trail and nothing here deletes a row.
/// <para>
/// The window is measured from <c>UpdatedAt</c>, and a completed task the sidecar still holds keeps
/// refreshing it on every checkpoint tick — so the clock starts when the engine stops returning a
/// snapshot for the torrent, not when the download finished. That is the intent: while the engine
/// still knows the torrent, its checkpoint is live data.
/// </para>
/// <para>
/// <see cref="DownloadState.Seeding"/> is excluded on purpose even though the task is past
/// downloading — a seeding torrent is still live in the sidecar. One the sidecar lost is closed as
/// <see cref="DownloadState.Removed"/> by the checkpoint pass, and is trimmed from then on.
/// </para>
/// <para>
/// Idempotent: the predicate requires a checkpoint to still be present, so a second run finds
/// nothing and reports zero.
/// </para>
/// </summary>
public sealed class TrimCheckpointsCommandHandler(
    DownloadsDbContext dbContext,
    DownloadRetentionOptions options,
    RetentionOptions platformOptions,
    ILogger<TrimCheckpointsCommandHandler> logger)
    : ICommandHandler<TrimCheckpointsCommand>
{
    public async Task<Result> HandleAsync(
        TrimCheckpointsCommand command,
        CancellationToken cancellationToken = default)
    {
        var cutoff = DateTimeOffset.UtcNow - options.CheckpointRetention;

        var trimmed = await RetentionPurge.UpdateInBatchesAsync(
            dbContext.Tasks,
            task => task.ResumeData != null
                && task.UpdatedAt < cutoff
                && (task.State == DownloadState.Completed
                    || task.State == DownloadState.Removed
                    || task.State == DownloadState.Error),
            task => task.Id,
            setters => setters.SetProperty(task => task.ResumeData, (byte[]?)null),
            platformOptions.BatchSize,
            cancellationToken);

        logger.LogInformation(
            "Retention trim (downloads): released the resume checkpoint of {Trimmed} finished downloads.",
            trimmed);

        return Result.Success();
    }
}
