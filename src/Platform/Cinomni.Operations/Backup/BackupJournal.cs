using Cinomni.Kernel.Identifiers;
using Cinomni.Kernel.Results;
using Cinomni.Operations.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cinomni.Operations.Backup;

/// <summary>The last run this installation attempted, as the backup worker needs to see it.</summary>
/// <param name="StartedAt">When that attempt began.</param>
/// <param name="Outcome">One of <see cref="BackupRunOutcome"/>.</param>
public sealed record BackupRunSummary(DateTimeOffset StartedAt, string Outcome);

/// <summary>
/// Writes what became of each backup run into <c>operations.backup_run</c>.
/// <para>
/// Every write here is <b>best effort</b>, and that is a deliberate ordering decision rather than
/// sloppiness: the dump on disk is the artefact, this row is how it is found later. A run must never be
/// reported as failed because its bookkeeping could not be written — and the bookkeeping genuinely can
/// be unavailable, because <c>backup create</c> runs before the migrate sequence so that
/// <c>backup check</c> can see an empty database. A failed write is logged with its reason and the
/// entity is detached, so a broken row can never ride along on somebody else's later save.
/// </para>
/// <para>
/// Each write is its own short transaction, taken after the external effect it describes rather than
/// around it: a run holds no unit of work while a child process writes gigabytes.
/// </para>
/// </summary>
public sealed class BackupJournal(OperationsDbContext dbContext, ILogger<BackupJournal> logger)
{
    /// <summary>How much of a failure reason is kept — the column's width, and the runner already truncates.</summary>
    private const int MaxReasonLength = 500;

    /// <summary>
    /// Opens a run. Returns the row when it was written, or <c>null</c> when the journal is unavailable,
    /// in which case the run proceeds unrecorded.
    /// </summary>
    public async Task<BackupRun?> BeginAsync(
        string triggeredBy,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken = default)
    {
        var run = new BackupRun
        {
            Id = Uuid7.New(startedAt),
            TriggeredBy = triggeredBy,
            Outcome = BackupRunOutcome.Running,
            StartedAt = startedAt,
        };

        return await WriteAsync(run, "open a backup run", cancellationToken) ? run : null;
    }

    /// <summary>Closes a run that produced a backup.</summary>
    public Task CompleteAsync(
        BackupRun? run,
        string stamp,
        BackupManifest manifest,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (run is null)
        {
            return Task.CompletedTask;
        }

        run.Outcome = BackupRunOutcome.Succeeded;
        run.CompletedAt = completedAt;
        run.Stamp = stamp;
        run.DumpSizeBytes = manifest.DumpSizeBytes;
        run.DumpSha256 = manifest.DumpSha256;
        run.Reason = null;

        return WriteAsync(run, "close a backup run", cancellationToken);
    }

    /// <summary>Closes a run that produced nothing, keeping the reason it did not.</summary>
    public Task FailAsync(
        BackupRun? run,
        string outcome,
        Error error,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default)
    {
        if (run is null)
        {
            return Task.CompletedTask;
        }

        run.Outcome = outcome;
        run.CompletedAt = completedAt;
        run.Reason = Reason(error);

        return WriteAsync(run, "close a backup run", cancellationToken);
    }

    /// <summary>
    /// Records a run that never started because another one held the advisory lock. Written as a
    /// complete row rather than a <c>Running</c> one: nothing is under way, and the point of the row is
    /// that a tick which produced no backup is not mistaken for one that did.
    /// </summary>
    public Task RecordSkippedAsync(
        string triggeredBy,
        Error error,
        DateTimeOffset at,
        CancellationToken cancellationToken = default) =>
        WriteAsync(
            new BackupRun
            {
                Id = Uuid7.New(at),
                TriggeredBy = triggeredBy,
                Outcome = BackupRunOutcome.Skipped,
                StartedAt = at,
                CompletedAt = at,
                Reason = Reason(error),
            },
            "record a skipped backup run",
            cancellationToken);

    /// <summary>
    /// Closes runs left <c>Running</c> by a crash. Only ever called by a run that <b>holds</b> the
    /// advisory lock, which is what makes it safe: no other run can be writing to those rows.
    /// </summary>
    public async Task<int> CloseAbandonedAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await dbContext.BackupRuns
                .Where(run => run.Outcome == BackupRunOutcome.Running)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(run => run.Outcome, BackupRunOutcome.Interrupted)
                        .SetProperty(run => run.CompletedAt, now)
                        .SetProperty(
                            run => run.Reason,
                            "The run did not finish; a later run found it open and closed it."),
                    cancellationToken);
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            Report(exception, "close abandoned backup runs");
            return 0;
        }
    }

    /// <summary>
    /// The most recent attempt, or <c>null</c> when this installation has never run one — and also when
    /// the journal cannot be read, which is indistinguishable from here. The worker keeps a floor of its
    /// own precisely because those two answers look alike: see <see cref="BackupHostedService"/>.
    /// </summary>
    public async Task<BackupRunSummary?> ReadLastRunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await dbContext.BackupRuns
                .AsNoTracking()
                .OrderByDescending(run => run.StartedAt)
                .Select(run => new BackupRunSummary(run.StartedAt, run.Outcome))
                .FirstOrDefaultAsync(cancellationToken);
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            Report(exception, "read the last backup run");
            return null;
        }
    }

    private static string Reason(Error error)
    {
        var reason = $"[{error.Code}] {error.Message}";
        return reason.Length <= MaxReasonLength ? reason : reason[..MaxReasonLength];
    }

    private async Task<bool> WriteAsync(BackupRun run, string what, CancellationToken cancellationToken)
    {
        try
        {
            if (dbContext.Entry(run).State == EntityState.Detached)
            {
                dbContext.BackupRuns.Add(run);
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            // Detach it, or the failed insert is retried by the next unrelated save on this scope.
            dbContext.Entry(run).State = EntityState.Detached;
            Report(exception, what);
            return false;
        }
    }

    private static bool IsUnavailable(Exception exception) =>
        exception is DbUpdateException or Npgsql.NpgsqlException or InvalidOperationException;

    private void Report(Exception exception, string what) =>
        logger.LogWarning(
            exception,
            "Could not {What}: the backup itself is unaffected, but this run will not appear in "
            + "operations.backup_run.",
            what);
}
