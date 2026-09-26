using Cinomni.Operations.Persistence;
using Cinomni.Operations.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cinomni.Operations.Backup;

/// <summary>
/// Takes the periodic backup, on a worker of its own.
/// <para>
/// Deliberately <b>not</b> a scheduled job enqueuing a command. There is one command worker and it
/// dispatches its claimed batch strictly sequentially, so a dump — minutes on a small installation,
/// far longer on a large one, and up to <c>Backup:Timeout</c> before it is killed — would stall every
/// other module's queued work behind it. That includes the download checkpoint on its thirty-second
/// cadence, whose resume data is the one thing in this installation that cannot be re-fetched. A
/// backup whose whole purpose is not losing data must not cost data while it runs.
/// </para>
/// <para>
/// What the queue was providing is provided here instead, and by the same mechanisms as before:
/// mutual exclusion is the PostgreSQL session advisory lock <see cref="BackupService"/> holds, which
/// also covers an operator running <c>backup create</c> from another process; the cadence survives a
/// restart because it is computed from <c>operations.backup_run</c> rather than from a timer; and
/// every attempt — including one that produced nothing — is persisted there instead of in the command
/// history.
/// </para>
/// </summary>
public sealed class BackupHostedService(
    IServiceScopeFactory scopeFactory,
    ILiveOptions<BackupOptions> options,
    ILogger<BackupHostedService> logger)
    : BackgroundService
{
    /// <summary>
    /// How long after startup the first check happens. A restart is a bad moment to launch a dump —
    /// migrations have just run and the modules are recovering — and a crash loop must not turn into
    /// one dump per restart.
    /// </summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Longest single sleep. A due time is re-derived from the journal at least this often, so a run
    /// taken by another process (an operator's <c>backup create</c>) moves this worker's next attempt
    /// without it having to be told.
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The floor this process keeps on its own attempts, mirroring the rule in
    /// <see cref="BackupSchedule"/>. The journal is the source of truth for the cadence, and it can be
    /// unreadable; without this, a database that cannot answer "when was the last run" would be
    /// answered with "never" on every tick, and that means a dump on every tick.
    /// </summary>
    private DateTimeOffset? _earliestNextAttempt;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var wait = await TimeUntilDueAsync(stoppingToken);

                    if (wait > TimeSpan.Zero)
                    {
                        await Task.Delay(wait < PollInterval ? wait : PollInterval, stoppingToken);
                        continue;
                    }

                    await RunOnceAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    // The worker outlives its own failures. A BackgroundService that lets an exception
                    // escape stops the host, and losing the whole installation because a backup tick
                    // could not decide when the next one is due is the wrong trade by a wide margin.
                    logger.LogError(exception, "The backup worker's tick failed; it will try again.");
                    await Task.Delay(PollInterval, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down between two backups is the normal way this worker ends.
        }
    }

    /// <summary>
    /// How long until the next run is due. A failure to read the journal is not fatal: the floor this
    /// worker keeps in memory still applies, so the worst case is one attempt per retry delay.
    /// <para>
    /// Reads <c>options.Current.Interval</c> fresh on every tick rather than through a
    /// <c>ScheduledJobRegistration</c> captured once at composition: unlike every other retention
    /// owner's cadence, a change to <c>retention.backup.interval</c> through the settings store applies
    /// on this worker's next poll, bounded by <see cref="PollInterval"/>.
    /// </para>
    /// </summary>
    private async Task<TimeSpan> TimeUntilDueAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        await using var scope = scopeFactory.CreateAsyncScope();
        var journal = scope.ServiceProvider.GetRequiredService<BackupJournal>();

        var due = BackupSchedule.NextDueAt(await journal.ReadLastRunAsync(cancellationToken), options.Current.Interval, now);

        if (_earliestNextAttempt is { } floor && floor > due)
        {
            due = floor;
        }

        return due <= now ? TimeSpan.Zero : due - now;
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var outcome = BackupRunOutcome.Failed;

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<BackupService>();

            var result = await service.CreateAsync(BackupTrigger.Scheduled, cancellationToken);

            if (result.IsSuccess)
            {
                outcome = BackupRunOutcome.Succeeded;
                logger.LogInformation(
                    "Scheduled backup {DumpFileName} completed ({Bytes} bytes).",
                    result.Value.DumpFileName,
                    result.Value.DumpSizeBytes);
            }
            else if (string.Equals(result.Error.Code, BackupService.InProgressCode, StringComparison.Ordinal))
            {
                outcome = BackupRunOutcome.Skipped;
            }
            else
            {
                logger.LogError(
                    "The scheduled backup failed [{ErrorCode}]: {Reason} It is recorded in "
                    + "operations.backup_run and will be attempted again in {RetryDelay}.",
                    result.Error.Code,
                    result.Error.Message,
                    BackupSchedule.DelayAfter(BackupRunOutcome.Failed, options.Current.Interval));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The run was interrupted by shutdown. Its journal row stays Running and the next run,
            // which alone can hold the advisory lock, closes it as interrupted.
            _earliestNextAttempt = startedAt + BackupSchedule.DelayAfter(outcome, options.Current.Interval);
            throw;
        }
        catch (Exception exception)
        {
            // The service returns operational failures as results; anything reaching here is a defect,
            // and it must not take the worker down with it.
            logger.LogError(exception, "The scheduled backup ended unexpectedly; it will be attempted again.");
        }

        _earliestNextAttempt = startedAt + BackupSchedule.DelayAfter(outcome, options.Current.Interval);
    }
}
