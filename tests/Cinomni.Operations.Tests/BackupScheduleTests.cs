using Cinomni.Operations.Backup;
using Cinomni.Operations.Persistence;
using Xunit;

namespace Cinomni.Operations.Tests;

/// <summary>
/// The backup cadence, as a rule rather than a timer. This is what replaced the scheduled job's
/// <c>next_due</c> column, so the properties that column gave for free — a restart does not reset the
/// clock, a missed window is taken on the way back up — are asserted here instead.
/// </summary>
public sealed class BackupScheduleTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 29, 21, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan Daily = TimeSpan.FromDays(1);

    /// <summary>An installation that has never backed itself up does it now, not tomorrow.</summary>
    [Fact]
    public void A_first_run_is_due_immediately()
    {
        Assert.Equal(Now, BackupSchedule.NextDueAt(lastRun: null, Daily, Now));
    }

    /// <summary>
    /// Measured from when the last attempt <b>started</b>, so a dump that takes two hours does not push
    /// the next one two hours later every day.
    /// </summary>
    [Fact]
    public void The_next_run_is_an_interval_after_the_last_one_started()
    {
        var last = new BackupRunSummary(Now.AddHours(-3), BackupRunOutcome.Succeeded);

        Assert.Equal(Now.AddHours(-3) + Daily, BackupSchedule.NextDueAt(last, Daily, Now));
    }

    /// <summary>
    /// A window missed while the installation was down is due the moment it comes back: the rule reads
    /// persisted state, so it cannot silently skip a day.
    /// </summary>
    [Fact]
    public void A_run_missed_while_the_installation_was_down_is_due_at_once()
    {
        var last = new BackupRunSummary(Now.AddDays(-4), BackupRunOutcome.Succeeded);

        Assert.True(BackupSchedule.NextDueAt(last, Daily, Now) <= Now);
    }

    /// <summary>
    /// A failed backup is retried inside the day rather than a full interval later. An installation that
    /// could not write a dump because a disk was briefly full must not stay unprotected until tomorrow.
    /// </summary>
    [Theory]
    [InlineData(BackupRunOutcome.Failed)]
    [InlineData(BackupRunOutcome.Interrupted)]
    [InlineData(BackupRunOutcome.Running)]
    public void A_run_that_produced_nothing_by_accident_is_retried_sooner(string outcome)
    {
        var last = new BackupRunSummary(Now, outcome);

        Assert.Equal(Now + BackupSchedule.RetryDelay, BackupSchedule.NextDueAt(last, Daily, Now));
    }

    /// <summary>
    /// Losing the advisory-lock race is not an accident: another run is taking the backup, so the
    /// cadence is being kept and retrying in fifteen minutes would only lose the race again.
    /// </summary>
    [Fact]
    public void A_skipped_run_waits_the_full_interval()
    {
        var last = new BackupRunSummary(Now, BackupRunOutcome.Skipped);

        Assert.Equal(Now + Daily, BackupSchedule.NextDueAt(last, Daily, Now));
    }

    /// <summary>The retry never waits longer than the cadence itself, however short that is configured.</summary>
    [Fact]
    public void A_retry_never_waits_longer_than_the_configured_interval()
    {
        var interval = TimeSpan.FromMinutes(5);
        var last = new BackupRunSummary(Now, BackupRunOutcome.Failed);

        Assert.Equal(Now + interval, BackupSchedule.NextDueAt(last, interval, Now));
    }
}
