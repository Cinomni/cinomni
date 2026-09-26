using Cinomni.Import.Application;
using Cinomni.Import.Contracts;
using Cinomni.Import.Files;
using Cinomni.Import.Persistence;
using Cinomni.Kernel.Messaging;
using Cinomni.Kernel.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cinomni.Import.Messaging;

/// <summary>
/// Runs <see cref="RetryImportJobCommand"/> through the import processor (⇠ startup recovery). The
/// command carries only the job id, because everything the drive needs is already on the job — the
/// download it imports, the correlation it echoes, the staging path it scans and the unit scope it
/// resolves against. Reconstructing that from a command payload would let the two disagree.
/// </summary>
public sealed class RetryImportJobCommandHandler(
    ImportDbContext dbContext,
    IImportProcessor processor,
    ImportOptions options,
    ILogger<RetryImportJobCommandHandler> logger)
    : ICommandHandler<RetryImportJobCommand>
{
    public async Task<Result> HandleAsync(RetryImportJobCommand command, CancellationToken cancellationToken = default)
    {
        var job = await dbContext.Jobs
            .AsNoTracking()
            .FirstOrDefaultAsync(j => j.Id == command.ImportJobId, cancellationToken);

        // Both are ordinary outcomes rather than failures. The queue is at-least-once, and a job that
        // finished between the enqueue and the dispatch — the original hand-off ran first — has
        // nothing left to do. Failing here would retry the command five times over a job that is
        // already registered, and then record a failure that never happened.
        if (job is null)
        {
            logger.LogInformation("Import job {JobId} is gone; nothing to re-drive.", command.ImportJobId);
            return Result.Success();
        }

        if (job.State is not ImportJobState.Pending)
        {
            logger.LogInformation(
                "Import job {JobId} is {State} and no longer needs a re-drive.", job.Id, job.State);
            return Result.Success();
        }

        // The source path is re-confined on every re-drive, not only on the drive that opened the job.
        // What is being replayed here is a path composed from a torrent's own name, read back out of
        // the database at startup — possibly long after the download, and possibly out of a restored
        // or edited backup. A path that will not confine is refused rather than scanned: the job stays
        // where it is, and the refusal is on record.
        if (!IsInsideTheStagingRoot(job.SourcePath))
        {
            logger.LogWarning(
                "Import job {JobId} was not re-driven: its source path is outside the configured staging "
                + "root. Nothing was scanned and the job was left as it is.",
                job.Id);
            return Result.Success();
        }

        await processor.ProcessCompletedDownloadAsync(
            job.DownloadTaskId,
            job.IntentId,
            job.AttemptId,
            job.WorkId,
            job.TargetId,
            job.SourcePath,
            job.RequestedUnitIds,
            cancellationToken);
        return Result.Success();
    }

    /// <summary>
    /// Whether a persisted source path may be handed to the scanner. An installation with no staging
    /// root configured is answered yes: the setting is opt-in so that adding it cannot stop an
    /// existing deployment from importing, and the composition root supplies it by default.
    /// </summary>
    private bool IsInsideTheStagingRoot(string sourcePath) =>
        options.StagingRoot.Length == 0 || PathGuard.IsStrictlyWithin(options.StagingRoot, sourcePath);
}
