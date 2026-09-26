using Cinomni.Operations.Persistence;

namespace Cinomni.Operations.Backup;

/// <summary>
/// When the next backup is due, from what the journal says about the last one.
/// <para>
/// A pure rule with the clock passed in, so the cadence is a unit test rather than something only a
/// two-hour integration run could observe. The state it reads is persisted, which is what makes the
/// cadence survive a restart: an installation that reboots hourly must not dump hourly, and one that
/// was down when a backup was due must take it on the way back up.
/// </para>
/// </summary>
public static class BackupSchedule
{
    /// <summary>
    /// How long a run that produced nothing waits before trying again, capped at the interval.
    /// <para>
    /// A failed backup is the state that matters most here: waiting a full day to retry a dump that
    /// failed because a disk was briefly full leaves the installation unprotected for that day, and
    /// retrying immediately would spin against a permanent fault. Bounded and fixed is the honest
    /// middle, and every attempt is recorded either way.
    /// </para>
    /// </summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(15);

    /// <summary>
    /// When a run should next start. A first-ever run is due immediately; anything else is due one
    /// interval after the last attempt began, or one retry delay after an attempt that produced nothing.
    /// </summary>
    /// <param name="lastRun">The most recent attempt, or <c>null</c> when there is none.</param>
    /// <param name="interval">The configured cadence.</param>
    /// <param name="now">The current instant, used when there is nothing to measure from.</param>
    public static DateTimeOffset NextDueAt(BackupRunSummary? lastRun, TimeSpan interval, DateTimeOffset now)
    {
        if (lastRun is null)
        {
            return now;
        }

        return lastRun.StartedAt + DelayAfter(lastRun.Outcome, interval);
    }

    /// <summary>How long to wait after a run with this outcome before attempting the next one.</summary>
    public static TimeSpan DelayAfter(string outcome, TimeSpan interval) => outcome switch
    {
        // A skip means another run was taking the backup, so the cadence is already being kept.
        BackupRunOutcome.Succeeded or BackupRunOutcome.Skipped => interval,
        _ => interval < RetryDelay ? interval : RetryDelay,
    };
}
