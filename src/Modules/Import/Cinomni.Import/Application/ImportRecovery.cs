using Cinomni.Import.Contracts;
using Cinomni.Import.Messaging;
using Cinomni.Import.Persistence;
using Cinomni.Operations.Messaging;
using Cinomni.Operations.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cinomni.Import.Application;

/// <summary>
/// Startup recovery for Import: finds the jobs a crash left in <see cref="ImportJobState.Pending"/>
/// and queues one re-drive for each.
/// <para>
/// Without it those jobs are unreachable for ever. Import's reaction to <c>DownloadCompleted</c> is
/// keyed <c>process-completed-download:{downloadTaskId}</c>, and the command queue drops a repeat of
/// a spent key — so the download that opened the job cannot announce itself a second time, and
/// nothing else in the product ever looks at a Pending job. That is not a rare corner: the import
/// FSM deliberately returns a job to Pending when part of a season pack fails to land, precisely so
/// it can be tried again.
/// </para>
/// <para>
/// The queued command is the hand-off, not the work. Scanning, hardlinking and probing are
/// out-of-process side effects that must be retryable and must not run inside the startup sequence:
/// recovery only records the decision to try again and lets the ordinary command worker
/// carry it out once the installation is serving.
/// </para>
/// <para>
/// The pass is bounded in three ways, because it runs before the installation serves anything. It
/// selects ids under a SQL predicate rather than materialising every stranded job with its matches
/// and its whole history graph; it takes at most <see cref="MaxJobsPerPass"/> of them, leaving the
/// rest to the next start; and a job that has spent its re-drives is parked for a person instead of
/// being tried again at every boot for the life of the installation.
/// </para>
/// </summary>
public sealed class ImportRecovery(
    ImportDbContext dbContext,
    IUnitOfWork unitOfWork,
    ICommandQueue commandQueue,
    ILogger<ImportRecovery> logger)
{
    /// <summary>
    /// How many stranded jobs one start will pick up. A backlog is not lost — the next start takes the
    /// next batch, oldest first — and startup must not grow without limit with the size of it.
    /// </summary>
    private const int MaxJobsPerPass = 200;

    /// <summary>
    /// Queues a re-drive for every stranded job and returns how many were queued. Safe to run twice:
    /// each pass claims its own attempt number, so a repeat inside one process queues a genuinely new
    /// attempt rather than a silent duplicate, and the drive itself is idempotent — a file that
    /// already verified is skipped instead of being linked again.
    /// </summary>
    public async Task<int> RecoverAsync(CancellationToken cancellationToken = default)
    {
        // Ids under a predicate, oldest first: an installation that accumulated stranded pack jobs
        // must not pull every match and history row it owns into memory to decide what to re-drive.
        var stranded = await dbContext.Jobs
            .AsNoTracking()
            .Where(j => j.State == ImportJobState.Pending)
            .Where(j => !j.Matches.Any() || j.Matches.Any(m => !LandedStates.Contains(m.State)))
            .OrderBy(j => j.CreatedAt)
            .Select(j => j.Id)
            .Take(MaxJobsPerPass)
            .ToListAsync(cancellationToken);

        var queued = 0;
        var parked = 0;
        foreach (var id in stranded)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var job = await LoadAsync(id, cancellationToken);
            if (job is null || job.State is not ImportJobState.Pending)
            {
                continue; // it moved on while we were working through the list
            }

            if (job.IsRecoveryExhausted)
            {
                await ParkAsync(job, cancellationToken);
                parked += 1;
                continue;
            }

            if (await QueueRetryAsync(job, cancellationToken))
            {
                queued += 1;
            }
        }

        if (queued > 0 || parked > 0)
        {
            logger.LogInformation(
                "Import recovery queued {QueuedCount} of {StrandedCount} pending job(s) for a re-drive "
                + "and parked {ParkedCount} that had spent their attempts.",
                queued,
                stranded.Count,
                parked);
        }

        return queued;
    }

    /// <summary>
    /// The match states that mean the file is physically in the library. Expressed here rather than
    /// read off <c>ImportFileMatch.IsLanded</c> because this predicate has to run as SQL.
    /// </summary>
    private static readonly ImportFileMatchState[] LandedStates =
    [
        ImportFileMatchState.Operated, ImportFileMatchState.Probed, ImportFileMatchState.Registered,
    ];

    private Task<ImportJob?> LoadAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.Jobs.Include(j => j.History).FirstOrDefaultAsync(j => j.Id == id, cancellationToken);

    /// <summary>
    /// Stops re-driving a job that has spent its attempts and leaves it where a person will find it.
    /// The state change and its history line are one transaction; nothing else in the product has to
    /// be told, because <see cref="ImportJobState.Unmatched"/> is already the state the interface
    /// offers a manual import from.
    /// </summary>
    private async Task ParkAsync(ImportJob job, CancellationToken cancellationToken)
    {
        var historyBefore = job.History.Count;
        job.ExhaustRecovery(
            $"the import was re-driven {job.RecoveryAttempts} times without landing everything it matched",
            DateTimeOffset.UtcNow);

        logger.LogWarning(
            "Import job {JobId} has spent its {Attempts} recovery attempts and is now waiting for a manual "
            + "import; it will not be re-driven again on its own.",
            job.Id,
            job.RecoveryAttempts);

        await unitOfWork.ExecuteAsync(async token =>
        {
            dbContext.History.AddRange(job.History.Skip(historyBefore));
            await dbContext.SaveChangesAsync(token);
        }, cancellationToken);
    }

    /// <summary>
    /// Claims one attempt and queues its command in a single transaction. The counter is claimed
    /// first because the command's idempotency key is built from it, and they are written together so
    /// that a crash between the two cannot leave a command carrying a number the next pass would
    /// reuse — and the queue would then drop.
    /// <para>
    /// A queue that <b>refuses</b> the key is a different case and is not a failure: it means a
    /// command for this exact attempt already exists, so the attempt number is represented by live
    /// work. The transaction still commits, and the cost is one number.
    /// </para>
    /// </summary>
    private async Task<bool> QueueRetryAsync(ImportJob job, CancellationToken cancellationToken)
    {
        var historyBefore = job.History.Count;
        var attempt = job.BeginRecovery(DateTimeOffset.UtcNow);
        var accepted = false;

        await unitOfWork.ExecuteAsync(async token =>
        {
            // Children reached through the tracked aggregate's navigations carry client-assigned keys,
            // so EF would mis-track them as Modified — add them to their set explicitly.
            dbContext.History.AddRange(job.History.Skip(historyBefore));
            await dbContext.SaveChangesAsync(token);
            accepted = await commandQueue.EnqueueAsync(
                new RetryImportJobCommand(job.Id),
                idempotencyKey: $"retry-import-job:{job.Id}:{attempt}",
                token);
        }, cancellationToken);

        if (!accepted)
        {
            // Only reachable when this exact attempt was already queued — a second recovery pass in
            // one process, or a restart between the commit and the log line. Never silent: a dropped
            // command writes nothing and fails nothing, so this line is the only trace it leaves.
            logger.LogInformation(
                "Import job {JobId} attempt {Attempt} was already queued for a re-drive.", job.Id, attempt);
        }

        return accepted;
    }
}
