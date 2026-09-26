using Cinomni.Downloads.Application;
using Cinomni.Downloads.Contracts;
using Cinomni.Downloads.Persistence;

namespace Cinomni.Downloads.Tests;

/// <summary>
/// Pure unit tests for the limits a transfer now has: the control checks made before the engine is
/// touched, the stall clock, the end of a seeding the engine lost, and the configured defaults.
/// </summary>
public sealed class TransferLimitFsmTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Timeout = TimeSpan.FromHours(24);

    private static DownloadTask NewTask() =>
        DownloadTask.Create(
            intentId: Guid.NewGuid(),
            attemptId: Guid.NewGuid(),
            workId: Guid.NewGuid(),
            targetId: Guid.NewGuid(),
            releaseGuid: "release-1",
            downloadUrl: "magnet:?xt=urn:btih:abc",
            savePath: "/data/staging",
            policy: SeedingPolicy.Unbounded,
            now: Now);

    private static DownloadTask Seeding()
    {
        var task = NewTask();
        task.MarkDownloading(Now);
        task.MarkCompleted("/data/staging/Movie", Now);
        task.MarkSeeding(Now);
        return task;
    }

    [Fact]
    public void A_seeding_download_refuses_a_pause_before_anything_changes()
    {
        var task = Seeding();
        var history = task.History.Count;

        Assert.Throws<DownloadStateConflictException>(task.EnsureCanPause);
        Assert.Throws<DownloadStateConflictException>(() => task.Pause(Now));
        Assert.Equal(DownloadState.Seeding, task.State);
        Assert.Equal(history, task.History.Count);
    }

    [Fact]
    public void A_finished_download_refuses_a_resume()
    {
        var task = Seeding();

        Assert.Throws<DownloadStateConflictException>(() => task.EnsureCanResume(force: false));
        Assert.Throws<DownloadStateConflictException>(() => task.Resume(Now));
    }

    [Fact]
    public void A_held_download_still_answers_the_hold_before_the_state()
    {
        var task = NewTask();
        task.MarkDownloading(Now);
        task.HoldForNetwork("tunnel-device-missing", Now);

        Assert.Throws<NetworkHoldException>(() => task.EnsureCanResume(force: false));
        task.EnsureCanResume(force: true); // the override is not a state conflict
    }

    [Fact]
    public void A_download_that_does_not_move_stalls_once_the_timeout_has_passed()
    {
        var task = NewTask();
        task.MarkDownloading(Now);
        task.TrackProgress(0.2, engineWaiting: false, Now);
        task.UpdateProgress(0.2, 0, 0, 0, 0, 0, 0, 0, Now);

        task.TrackProgress(0.2, engineWaiting: false, Now + Timeout - TimeSpan.FromMinutes(1));
        Assert.False(task.HasStalled(Timeout, Now + Timeout - TimeSpan.FromMinutes(1)));

        task.TrackProgress(0.2, engineWaiting: false, Now + Timeout);
        Assert.True(task.HasStalled(Timeout, Now + Timeout));
    }

    [Fact]
    public void Any_progress_restarts_the_clock()
    {
        var task = NewTask();
        task.MarkDownloading(Now);
        task.TrackProgress(0.2, engineWaiting: false, Now);
        task.UpdateProgress(0.2, 0, 0, 0, 0, 0, 0, 0, Now);

        var later = Now + Timeout - TimeSpan.FromMinutes(1);
        task.TrackProgress(0.21, engineWaiting: false, later);

        Assert.False(task.HasStalled(Timeout, later + TimeSpan.FromMinutes(2)));
        Assert.True(task.HasStalled(Timeout, later + Timeout));
    }

    [Fact]
    public void Progress_that_rises_and_falls_back_does_not_keep_restarting_the_clock()
    {
        // Unverified blocks raise progress and a failed hash takes it back: a swarm that does that for
        // ever must not keep a dead download from stalling.
        var task = NewTask();
        task.MarkDownloading(Now);
        task.TrackProgress(0.30, engineWaiting: false, Now);
        task.UpdateProgress(0.30, 0, 0, 0, 0, 0, 0, 0, Now);

        for (var hour = 1; hour <= 24; hour++)
        {
            var at = Now + TimeSpan.FromHours(hour);
            var progress = hour % 2 == 0 ? 0.30 : 0.29;
            task.TrackProgress(progress, engineWaiting: false, at);
            task.UpdateProgress(progress, 0, 0, 0, 0, 0, 0, 0, at);
        }

        Assert.True(task.HasStalled(Timeout, Now + Timeout));
    }

    [Fact]
    public void A_download_the_engine_keeps_queued_is_waiting_not_stalled()
    {
        var task = NewTask();
        task.MarkDownloading(Now);
        task.TrackProgress(0, engineWaiting: false, Now);

        var much = Now + Timeout * 3;
        task.TrackProgress(0, engineWaiting: true, much);

        Assert.False(task.HasStalled(Timeout, much));
    }

    [Fact]
    public void A_paused_download_never_stalls_and_comes_back_with_a_fresh_clock()
    {
        var task = NewTask();
        task.MarkDownloading(Now);
        task.TrackProgress(0, engineWaiting: false, Now);
        task.Pause(Now);

        var resumedAt = Now + Timeout * 2;
        Assert.False(task.HasStalled(Timeout, resumedAt));

        task.Resume(resumedAt);
        Assert.False(task.HasStalled(Timeout, resumedAt + TimeSpan.FromHours(1)));
        Assert.True(task.HasStalled(Timeout, resumedAt + Timeout));
    }

    [Fact]
    public void A_released_network_hold_restarts_the_clock()
    {
        var task = NewTask();
        task.MarkDownloading(Now);
        task.TrackProgress(0, engineWaiting: false, Now);
        task.HoldForNetwork("tunnel-device-missing", Now);

        var releasedAt = Now + Timeout * 2;
        Assert.False(task.HasStalled(Timeout, releasedAt));

        task.ReleaseNetworkHold(releasedAt);
        Assert.Equal(DownloadState.Downloading, task.State);
        Assert.False(task.HasStalled(Timeout, releasedAt + TimeSpan.FromHours(1)));
    }

    [Fact]
    public void A_task_first_observed_long_after_it_was_created_starts_its_clock_then()
    {
        var task = NewTask();
        task.MarkDownloading(Now);
        var firstSeen = Now + Timeout * 5;

        task.TrackProgress(0, engineWaiting: false, firstSeen);

        Assert.False(task.HasStalled(Timeout, firstSeen));
    }

    [Fact]
    public void Checking_and_finished_downloads_never_stall()
    {
        var checking = NewTask();
        checking.MarkChecking(Now);
        checking.TrackProgress(0, engineWaiting: false, Now);
        Assert.False(checking.HasStalled(Timeout, Now + Timeout * 2));

        var seeding = Seeding();
        seeding.TrackProgress(1, engineWaiting: false, Now);
        Assert.False(seeding.HasStalled(Timeout, Now + Timeout * 2));
    }

    [Fact]
    public void Only_a_finished_download_ends_its_lost_seeding()
    {
        var seeding = Seeding();
        Assert.True(seeding.EndSeedingLost(Now));
        Assert.Equal(DownloadState.Removed, seeding.State);
        Assert.Equal("SeedingLost", seeding.History[^1].Trigger);
        Assert.False(seeding.EndSeedingLost(Now)); // already closed

        var downloading = NewTask();
        downloading.MarkDownloading(Now);
        Assert.False(downloading.EndSeedingLost(Now));
        Assert.Equal(DownloadState.Downloading, downloading.State);
    }

    [Fact]
    public void New_downloads_seed_to_a_ratio_of_one_or_a_week_by_default()
    {
        var policy = new TransferOptions().DefaultSeedingPolicy;

        Assert.Equal(1.0, policy.RatioLimit);
        Assert.Equal((int)TimeSpan.FromDays(7).TotalSeconds, policy.SeedTimeLimitSeconds);
    }

    [Fact]
    public void Both_seeding_bounds_can_be_lifted()
    {
        var options = new TransferOptions { SeedRatioLimit = null, SeedTimeLimit = null };
        options.Validate();

        Assert.Equal(SeedingPolicy.Unbounded, options.DefaultSeedingPolicy);
    }

    [Theory]
    [InlineData(0.0, 1.0, 1.0)]
    [InlineData(24.0, -1.0, 1.0)]
    [InlineData(24.0, 1.0, 0.0)]
    public void Unusable_limits_are_refused_at_startup(double stallHours, double ratio, double seedDays)
    {
        var options = new TransferOptions
        {
            StallTimeout = TimeSpan.FromHours(stallHours),
            SeedRatioLimit = ratio,
            SeedTimeLimit = TimeSpan.FromDays(seedDays),
        };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }
}
