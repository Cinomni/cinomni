using Cinomni.Downloads.Contracts;
using Cinomni.Downloads.Engine;
using Cinomni.Downloads.Persistence;

namespace Cinomni.Downloads.Tests;

/// <summary>
/// The network hold, as pure state-machine rules (no database, no engine).
/// <para>
/// These are the rules that decide whether a household's downloads survive a VPN outage, so they are
/// pinned here rather than only through the integration path: an installation that loses an intent
/// during an outage has lost work nothing can recover, and that failure has to be impossible to
/// reintroduce quietly.
/// </para>
/// </summary>
public sealed class TunnelHoldFsmTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = Now.AddMinutes(5);

    private const string Reason = "default-route-not-via-tunnel";

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

    private static DownloadTask Downloading()
    {
        var task = NewTask();
        task.MarkDownloading(Now);
        return task;
    }

    [Fact]
    public void Holding_a_downloading_task_pauses_it_and_records_why()
    {
        var task = Downloading();

        Assert.True(task.HoldForNetwork(Reason, Now));

        Assert.Equal(DownloadState.Paused, task.State);
        Assert.True(task.IsNetworkHeld);
        Assert.Equal(Now, task.NetworkHoldSince);
        Assert.Equal(Reason, task.NetworkHoldReason);

        // The explanation is on the trail as well as on the row: a pause with no recorded cause is
        // exactly what an operator cannot act on.
        var last = task.History[^1];
        Assert.Equal("NetworkHold", last.Trigger);
        Assert.Equal(Reason, last.Note);
        Assert.Equal(DownloadState.Paused, last.ToState);
    }

    [Fact]
    public void Holding_twice_changes_nothing()
    {
        var task = Downloading();
        task.HoldForNetwork(Reason, Now);
        var history = task.History.Count;

        Assert.False(task.HoldForNetwork(Reason, Later));

        Assert.Equal(history, task.History.Count);
        Assert.Equal(Now, task.NetworkHoldSince);
    }

    [Theory]
    [InlineData(DownloadState.Queued)]
    [InlineData(DownloadState.ResolvingMetadata)]
    [InlineData(DownloadState.Checking)]
    [InlineData(DownloadState.Downloading)]
    public void Every_in_flight_state_can_be_held(DownloadState from)
    {
        var task = NewTask();
        switch (from)
        {
            case DownloadState.ResolvingMetadata:
                task.MarkResolvingMetadata(Now);
                break;
            case DownloadState.Checking:
                task.MarkChecking(Now);
                break;
            case DownloadState.Downloading:
                task.MarkDownloading(Now);
                break;
            default:
                break;
        }

        Assert.True(task.HoldForNetwork(Reason, Now));
        Assert.Equal(DownloadState.Paused, task.State);
    }

    [Theory]
    [InlineData(DownloadState.Completed)]
    [InlineData(DownloadState.Removed)]
    public void A_finished_task_is_not_held(DownloadState terminal)
    {
        var task = Downloading();
        if (terminal is DownloadState.Completed)
        {
            task.MarkCompleted("/data/staging/Movie", Now);
        }
        else
        {
            task.Remove(Now);
        }

        Assert.False(task.HoldForNetwork(Reason, Now));
        Assert.False(task.IsNetworkHeld);
        Assert.Equal(terminal, task.State);
    }

    [Fact]
    public void A_held_task_refuses_a_manual_resume()
    {
        var task = Downloading();
        task.HoldForNetwork(Reason, Now);

        var refusal = Assert.Throws<NetworkHoldException>(() => task.Resume(Later));

        Assert.Equal(task.Id, refusal.DownloadTaskId);
        Assert.Equal(Reason, refusal.Reason);
        Assert.Equal(DownloadState.Paused, task.State);
    }

    [Fact]
    public void An_operator_override_resumes_and_is_written_down()
    {
        var task = Downloading();
        task.HoldForNetwork(Reason, Now);

        task.Resume(Later, force: true);

        Assert.Equal(DownloadState.Downloading, task.State);
        Assert.False(task.IsNetworkHeld);
        Assert.Equal("NetworkHoldOverride", task.History[^1].Trigger);
        Assert.NotNull(task.History[^1].Note);
    }

    [Fact]
    public void An_overridden_task_is_no_longer_released_by_the_tunnel_coming_back()
    {
        // The trap this closes: an override that left the hold in place would be resumed a second
        // time by the automatic release, re-emitting a transition for a task that already moved.
        var task = Downloading();
        task.HoldForNetwork(Reason, Now);
        task.Resume(Later, force: true);

        Assert.False(task.ReleaseNetworkHold(Later.AddMinutes(1)));
        Assert.Equal(DownloadState.Downloading, task.State);
    }

    [Fact]
    public void Releasing_a_hold_returns_the_task_to_downloading()
    {
        var task = Downloading();
        task.HoldForNetwork(Reason, Now);

        Assert.True(task.ReleaseNetworkHold(Later));

        Assert.Equal(DownloadState.Downloading, task.State);
        Assert.False(task.IsNetworkHeld);
        Assert.Null(task.NetworkHoldReason);
        Assert.Equal("NetworkRelease", task.History[^1].Trigger);
    }

    [Fact]
    public void Releasing_a_hold_twice_changes_nothing()
    {
        var task = Downloading();
        task.HoldForNetwork(Reason, Now);
        task.ReleaseNetworkHold(Later);
        var history = task.History.Count;

        Assert.False(task.ReleaseNetworkHold(Later.AddMinutes(1)));

        Assert.Equal(history, task.History.Count);
    }

    [Fact]
    public void A_task_paused_by_hand_before_the_outage_stays_paused_after_it()
    {
        // The hold must not become a way to undo somebody's decision. It goes on top of the manual
        // pause and comes off it, and the state it found is the state it leaves.
        var task = Downloading();
        task.Pause(Now);

        Assert.True(task.HoldForNetwork(Reason, Now));
        Assert.Equal(DownloadState.Paused, task.State);
        Assert.True(task.IsNetworkHeld);

        Assert.True(task.ReleaseNetworkHold(Later));

        Assert.Equal(DownloadState.Paused, task.State);
        Assert.False(task.IsNetworkHeld);
    }

    [Fact]
    public void Holding_never_fails_the_task()
    {
        // Emitting DownloadFailed here would send the acquisition goal back to searching for the
        // length of the outage and lose the transfer with it. A hold is not a failure.
        var task = Downloading();

        task.HoldForNetwork(Reason, Now);

        Assert.NotEqual(DownloadState.Error, task.State);
        Assert.Null(task.LastError);
        Assert.DoesNotContain(task.History, entry => entry.ToState is DownloadState.Error);
    }

    [Fact]
    public void A_task_that_was_never_held_ignores_a_release()
    {
        var task = Downloading();

        Assert.False(task.ReleaseNetworkHold(Later));
        Assert.Equal(DownloadState.Downloading, task.State);
    }
}

/// <summary>The observation record, which is what turns an unanswerable question into a safe answer.</summary>
public sealed class TunnelObservationTests
{
    [Fact]
    public void All_three_facts_are_needed_to_call_it_verified()
    {
        Assert.True(Observation(true, true, true).Verified);
        Assert.False(Observation(false, true, true).Verified);
        Assert.False(Observation(true, false, true).Verified);
        Assert.False(Observation(true, true, false).Verified);
    }

    [Fact]
    public void An_engine_that_did_not_answer_is_never_verified()
    {
        // Silence is not evidence of safety. This is the single most important line in the guard:
        // treating an unreachable sidecar as healthy would defeat the whole mechanism.
        var unreachable = TunnelObservation.Unreachable("tun0", DateTimeOffset.UnixEpoch);

        Assert.False(unreachable.Verified);
        Assert.Equal(TunnelObservation.UnreachableReason, unreachable.Reason);
        Assert.Equal("tun0", unreachable.TunnelDevice);
    }

    private static TunnelObservation Observation(bool up, bool routed, bool identity) =>
        new("tun0", up, routed, identity, "reason", "block", SessionHeld: false, DateTimeOffset.UnixEpoch);
}

/// <summary>Configuration validation: an unusable guard stops startup instead of pretending to work.</summary>
public sealed class TunnelOptionsTests
{
    [Fact]
    public void The_default_is_opt_out_and_fails_closed_once_opted_in()
    {
        var options = new TunnelOptions();

        Assert.False(options.IsConfigured);
        Assert.Equal(TunnelLossPolicy.Block, options.LossPolicy);
        options.Validate();
    }

    [Fact]
    public void A_configured_device_turns_the_guard_on()
    {
        var options = new TunnelOptions { Device = "tun0" };

        Assert.True(options.IsConfigured);
        options.Validate();
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("")]
    public void Blank_is_not_a_device(string device)
    {
        Assert.False(new TunnelOptions { Device = device }.IsConfigured);
    }

    [Fact]
    public void A_device_with_stray_whitespace_is_refused_rather_than_silently_trimmed()
    {
        // A name that does not match the interface the sidecar bound would verify an egress path
        // nothing is actually taking, which is worse than no guard at all.
        Assert.Throws<InvalidOperationException>(() => new TunnelOptions { Device = "tun0 " }.Validate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_threshold_below_one_is_refused(int threshold)
    {
        Assert.Throws<InvalidOperationException>(
            () => new TunnelOptions { UnverifiedThreshold = threshold }.Validate());
        Assert.Throws<InvalidOperationException>(
            () => new TunnelOptions { VerifiedThreshold = threshold }.Validate());
    }

    [Fact]
    public void A_non_positive_poll_interval_is_refused()
    {
        Assert.Throws<InvalidOperationException>(
            () => new TunnelOptions { PollInterval = TimeSpan.Zero }.Validate());
    }

    [Fact]
    public void An_unknown_policy_value_is_refused()
    {
        Assert.Throws<InvalidOperationException>(
            () => new TunnelOptions { LossPolicy = (TunnelLossPolicy)99 }.Validate());
    }
}
