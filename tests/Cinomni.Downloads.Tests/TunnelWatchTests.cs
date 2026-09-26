using System.Collections.Concurrent;
using Cinomni.Acquisition.Contracts;
using Cinomni.Downloads.Application;
using Cinomni.Downloads.Contracts;
using Cinomni.Downloads.Engine;
using Cinomni.Downloads.Messaging;
using Cinomni.Downloads.Persistence;
using Cinomni.Kernel.Identifiers;
using Cinomni.Kernel.Messaging;
using Cinomni.Operations.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Downloads.Tests;

/// <summary>
/// The egress guard against a real PostgreSQL instance: an observation becomes a durable hold, the
/// hold and its event are written in one committed operation, redelivery is a no-op, and a recovery
/// releases exactly what it held.
/// <para>
/// The acquisition spine is present because the most important assertion here is a negative one: a
/// held download must not fail, or Acquisition would return the goal to searching for the length of
/// the outage — burning indexer quota to find releases it then could not fetch either.
/// </para>
/// </summary>
public sealed class TunnelWatchTests : IAsyncLifetime
{
    private const string LeakReason = "egress-identity-not-the-tunnel";

    private readonly FakeTorrentEngine _engine = new();
    private readonly EgressSink _sink = new();
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync() =>
        _provider = await DownloadsTestHost.CreateAsync(
            "cinomni_test_tunnelwatch",
            _engine,
            services =>
            {
                services.AddSingleton(_sink);
                services.AddScoped<IEventHandler<TunnelEgressLost>, EgressLostSinkHandler>();
                services.AddScoped<IEventHandler<TunnelEgressRestored>, EgressRestoredSinkHandler>();
            },
            // Opted in, as a deployment does. PauseAndAlert so the hysteresis is exercised: Block
            // deliberately holds on the very first failed observation and would hide the counters.
            tunnel =>
            {
                tunnel.Device = "tun0";
                tunnel.LossPolicy = TunnelLossPolicy.PauseAndAlert;
                tunnel.UnverifiedThreshold = 2;
                tunnel.VerifiedThreshold = 2;
            });

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task A_single_failed_observation_holds_nothing_under_the_configured_threshold()
    {
        var attemptId = await ArrangeDownloadingTaskAsync();
        _engine.ObserveUnverified(LeakReason);

        await CheckAsync();

        var task = await GetTaskAsync(attemptId);
        Assert.Equal(DownloadState.Downloading, task!.State);
        Assert.False(task.IsNetworkHeld);
        Assert.Empty(_sink.Lost);

        // The streak is persisted, which is what lets the decision survive a restart mid-outage.
        var state = await GetStateAsync();
        Assert.Equal(1, state!.UnverifiedStreak);
        Assert.False(state.Holding);
    }

    [Fact]
    public async Task Crossing_the_threshold_holds_every_in_flight_download_and_says_why()
    {
        var attemptId = await ArrangeDownloadingTaskAsync();
        _engine.ObserveUnverified(LeakReason);

        await CheckAsync();
        await CheckAsync();

        var task = await GetTaskAsync(attemptId);
        Assert.Equal(DownloadState.Paused, task!.State);
        Assert.True(task.IsNetworkHeld);
        Assert.Equal(LeakReason, task.NetworkHoldReason);
        Assert.Equal("NetworkHold", task.History[^1].Trigger);
        Assert.Equal(LeakReason, task.History[^1].Note);
    }

    [Fact]
    public async Task The_hold_stops_every_held_transfer_at_the_engine()
    {
        // The durable half of the kill-switch is not enough on its own. In every case where the
        // sidecar's own session pause is absent — an engine too old to answer, a control credential
        // that does not match, a tunnel device it was never given — this call is the only thing that
        // actually stops the traffic the interface says is stopped.
        var attemptId = await ArrangeDownloadingTaskAsync();
        var infoHash = (await GetTaskAsync(attemptId))!.InfoHash!;
        _engine.ObserveUnverified(LeakReason);

        await CheckAsync();
        await CheckAsync();

        Assert.Contains(infoHash, _engine.Paused);
    }

    [Fact]
    public async Task The_release_starts_again_exactly_what_the_hold_stopped()
    {
        // A re-add of a torrent the engine already holds answers with the same info-hash, which is
        // what a real engine does and what the release below relies on.
        _engine.PinInfoHash = true;

        var attemptId = await ArrangeDownloadingTaskAsync();
        var infoHash = (await GetTaskAsync(attemptId))!.InfoHash!;
        _engine.ObserveUnverified(LeakReason);
        await CheckAsync();
        await CheckAsync();

        _engine.ObserveVerified();
        await CheckAsync();
        await CheckAsync();

        // Re-established from its checkpoint, not resumed. The commonest cause of a hold is the
        // sidecar process going away, and a sidecar that comes back holds nothing: a resume aimed at
        // an info-hash nobody holds succeeds, transfers nothing, and leaves the row saying Downloading
        // for ever with no timeout and no failure.
        Assert.Equal(2, _engine.Adds.Count);
        Assert.Equal([infoHash, infoHash], _engine.AddedInfoHashes.Order());
        Assert.Contains((await GetTaskAsync(attemptId))!.History, line => line.Trigger == "Recover");
    }

    [Fact]
    public async Task An_observation_too_old_to_stand_for_the_present_is_not_a_verification()
    {
        // The sidecar's guard thread died and its verdict froze: the answer still arrives, still says
        // verified, and is hours old. Nothing but its age distinguishes it from a fresh one.
        var attemptId = await ArrangeDownloadingTaskAsync();
        _engine.ObserveVerified();
        _engine.ObservedAt = DateTimeOffset.UtcNow.AddHours(-1);

        await CheckAsync();
        await CheckAsync();

        var task = await GetTaskAsync(attemptId);
        Assert.True(task!.IsNetworkHeld);
        Assert.Equal(TunnelObservationReasons.ObservationStale, task.NetworkHoldReason);
    }

    [Fact]
    public async Task A_sidecar_enforcing_a_weaker_policy_is_not_a_verified_egress()
    {
        // Both halves are configured separately. A sidecar on `ignore` never pauses its own session,
        // so a backend that believed its verified answer would report an installation as protected by
        // a policy only one of the two processes is applying.
        var attemptId = await ArrangeDownloadingTaskAsync();
        _engine.ObserveVerified();
        _engine.TunnelObservation = _engine.TunnelObservation with { Policy = "ignore" };

        await CheckAsync();
        await CheckAsync();

        var task = await GetTaskAsync(attemptId);
        Assert.True(task!.IsNetworkHeld);
        Assert.Equal(TunnelObservationReasons.PolicyDivergent, task.NetworkHoldReason);
    }

    [Fact]
    public async Task A_sidecar_watching_another_interface_is_not_a_verified_egress()
    {
        var attemptId = await ArrangeDownloadingTaskAsync();
        _engine.ObserveVerified();
        _engine.TunnelObservation = _engine.TunnelObservation with { TunnelDevice = "wg0" };

        await CheckAsync();
        await CheckAsync();

        Assert.Equal(
            TunnelObservationReasons.DeviceDivergent,
            (await GetTaskAsync(attemptId))!.NetworkHoldReason);
    }

    [Fact]
    public async Task A_sidecar_enforcing_a_stronger_policy_is_not_a_divergence()
    {
        // The overlay's own arrangement: this installation chose PauseAndAlert and the sidecar image
        // defaults to block. Nothing is at risk there, and treating it as a failure would hold every
        // download on a correctly configured deployment.
        await ArrangeDownloadingTaskAsync();
        _engine.ObserveVerified();
        _engine.TunnelObservation = _engine.TunnelObservation with { Policy = "block" };

        await CheckAsync();

        var state = await GetStateAsync();
        Assert.True(state!.Verified);
        Assert.Equal(TunnelObservation.VerifiedReason, state.Reason);
    }

    [Fact]
    public async Task Turning_the_guard_off_lets_go_of_what_it_was_holding()
    {
        // The way back to a direct connection: the operator gives up on the tunnel and restarts
        // without it. Nothing else in the installation ever clears a network hold, and a held task
        // still counts as in flight — so the alternative is downloads stopped for good and new
        // acquisitions attaching to a task that can never run.
        var attemptId = await ArrangeDownloadingTaskAsync();
        _engine.ObserveUnverified(LeakReason);
        await CheckAsync();
        await CheckAsync();
        Assert.True((await GetTaskAsync(attemptId))!.IsNetworkHeld);

        _provider.GetRequiredService<TunnelOptions>().Device = string.Empty;
        await CheckAsync();

        var task = await GetTaskAsync(attemptId);
        Assert.False(task!.IsNetworkHeld);
        Assert.Equal(DownloadState.Downloading, task.State);
        Assert.False((await GetStateAsync())!.Holding);

        // And the manual path is open again, rather than answering 409 for a tunnel that is gone.
        await using var scope = _provider.CreateAsyncScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<DownloadService>()
            .ResumeAsync(new DownloadTaskId(task.Id)));
    }

    [Fact]
    public async Task An_operator_can_force_a_resume_the_moment_the_guard_is_gone()
    {
        // The gap between removing the tunnel and the next watch cycle. The row still carries the
        // hold, but no policy is in force to refuse an override any more — answering 409 on behalf of
        // a guard the installation no longer has is the trap the release path exists to avoid.
        var attemptId = await ArrangeDownloadingTaskAsync();
        _engine.ObserveUnverified(LeakReason);
        await CheckAsync();
        await CheckAsync();
        var id = new DownloadTaskId((await GetTaskAsync(attemptId))!.Id);

        _provider.GetRequiredService<TunnelOptions>().Device = string.Empty;

        await using (var scope = _provider.CreateAsyncScope())
        {
            Assert.True(await scope.ServiceProvider.GetRequiredService<DownloadService>()
                .ResumeAsync(id, force: true));
        }

        var task = await GetTaskAsync(attemptId);
        Assert.False(task!.IsNetworkHeld);
        Assert.Equal("NetworkHoldOverride", task.History[^1].Trigger);
    }

    [Fact]
    public async Task Weakening_the_policy_to_Ignore_mid_outage_lets_go_of_the_holds()
    {
        // What the drop drill's negative control asks an operator to do. Under Ignore the guard takes
        // no hold; it must still lift one it already has, or the drill strands the installation.
        var attemptId = await ArrangeDownloadingTaskAsync();
        _engine.ObserveUnverified(LeakReason);
        await CheckAsync();
        await CheckAsync();
        Assert.True((await GetTaskAsync(attemptId))!.IsNetworkHeld);

        _provider.GetRequiredService<TunnelOptions>().LossPolicy = TunnelLossPolicy.Ignore;
        await CheckAsync();

        Assert.False((await GetTaskAsync(attemptId))!.IsNetworkHeld);
        // Still observed and still reported: the operator asked to see this, they only stopped asking
        // Cinomni to act on it.
        var state = await GetStateAsync();
        Assert.False(state!.Holding);
        Assert.False(state.Verified);
        Assert.Equal(LeakReason, state.Reason);
    }

    [Fact]
    public async Task The_hold_and_its_event_are_one_committed_operation()
    {
        var attemptId = await ArrangeDownloadingTaskAsync();
        _engine.ObserveUnverified(LeakReason);

        await CheckAsync();
        await CheckAsync();

        // Before any dispatch: the task is held and the message is already in the outbox. Neither can
        // exist without the other, which is what makes the state and the operator's alert agree.
        Assert.True((await GetTaskAsync(attemptId))!.IsNetworkHeld);
        Assert.Equal(1, await CountOutboxAsync(DownloadEventNames.TunnelEgressLost));

        await DrainAsync();

        var lost = Assert.Single(_sink.Lost);
        Assert.Equal(LeakReason, lost.Reason);
        Assert.Equal(1, lost.HeldTaskCount);
        Assert.Equal(1, lost.Sequence);
        Assert.Equal(nameof(TunnelLossPolicy.PauseAndAlert), lost.Policy);
    }

    [Fact]
    public async Task Continuing_to_observe_the_same_outage_does_not_declare_a_second_one()
    {
        await ArrangeDownloadingTaskAsync();
        _engine.ObserveUnverified(LeakReason);

        for (var poll = 0; poll < 5; poll++)
        {
            await CheckAsync();
        }

        await DrainAsync();

        Assert.Single(_sink.Lost);
        Assert.Equal(1, (await GetStateAsync())!.TransitionSequence);
    }

    [Fact]
    public async Task Redelivering_the_loss_raises_one_notification_command()
    {
        await ArrangeDownloadingTaskAsync();
        _engine.ObserveUnverified(LeakReason);
        await CheckAsync();
        await CheckAsync();
        await DrainAsync();

        // The same event, delivered again. Delivery is at-least-once, so the handler genuinely runs
        // twice — what must not happen twice is the side effect, and the transition sequence in the
        // key is what stops it. This is exactly what Notifications' handler does with the same key.
        await PublishAsync(new TunnelEgressLost(1, LeakReason, 1, nameof(TunnelLossPolicy.PauseAndAlert)));
        await DrainAsync();

        Assert.Equal(2, _sink.Lost.Count);
        Assert.Equal([1], _sink.Notified);
    }

    [Fact]
    public async Task Recovery_releases_exactly_what_was_held_and_publishes_the_pair()
    {
        _engine.PinInfoHash = true; // a re-add of the same torrent answers with the same info-hash
        var attemptId = await ArrangeDownloadingTaskAsync();
        _engine.ObserveUnverified(LeakReason);
        await CheckAsync();
        await CheckAsync();

        _engine.ObserveVerified();
        await CheckAsync();
        Assert.True((await GetTaskAsync(attemptId))!.IsNetworkHeld); // one verified observation is not enough
        await CheckAsync();

        var task = await GetTaskAsync(attemptId);
        Assert.Equal(DownloadState.Downloading, task!.State);
        Assert.False(task.IsNetworkHeld);
        // The release, and then the re-establish it drives: the trail says both, in that order.
        Assert.Equal(
            ["NetworkRelease", "Recover"],
            task.History.TakeLast(2).Select(line => line.Trigger));

        await DrainAsync();
        var restored = Assert.Single(_sink.Restored);
        Assert.Equal(1, restored.ReleasedTaskCount);
        // The same sequence as the loss it ends: one outage, one pair.
        Assert.Equal(Assert.Single(_sink.Lost).Sequence, restored.Sequence);
    }

    [Fact]
    public async Task A_second_outage_is_a_new_transition_and_is_not_deduplicated_away()
    {
        await ArrangeDownloadingTaskAsync();

        _engine.ObserveUnverified(LeakReason);
        await CheckAsync();
        await CheckAsync();
        _engine.ObserveVerified();
        await CheckAsync();
        await CheckAsync();
        _engine.ObserveUnverified("tunnel-device-missing");
        await CheckAsync();
        await CheckAsync();
        await DrainAsync();

        Assert.Equal(2, _sink.Lost.Count);
        Assert.Equal([1, 2], _sink.Lost.Select(e => e.Sequence).Order());
    }

    [Fact]
    public async Task A_held_download_never_fails_so_the_acquisition_goal_stays_put()
    {
        var targetId = Uuid7.New();
        var attemptId = await ArrangeDownloadingTaskAsync(targetId);
        var infoHash = _engine.NextInfoHash;
        _engine.ObserveUnverified(LeakReason);
        await CheckAsync();
        await CheckAsync();

        // The engine reports the failure the dropped tunnel caused. A held task absorbs the figures
        // and refuses the transition, so DownloadFailed is never published.
        await ApplyStatusAsync(FakeTorrentEngine.Snapshot(infoHash, "downloading", error: "connection refused"));
        await DrainAsync();

        var task = await GetTaskAsync(attemptId);
        Assert.Equal(DownloadState.Paused, task!.State);
        Assert.Null(task.LastError);
        Assert.Equal(0, await CountOutboxAsync(DownloadEventNames.DownloadFailed));
        Assert.Equal(IntentState.Downloading, await GetIntentStateAsync(targetId));
    }

    [Fact]
    public async Task A_held_download_refuses_a_manual_resume_with_the_reason()
    {
        var attemptId = await ArrangeDownloadingTaskAsync();
        _engine.ObserveUnverified(LeakReason);
        await CheckAsync();
        await CheckAsync();
        var id = new DownloadTaskId((await GetTaskAsync(attemptId))!.Id);

        var refusal = await Assert.ThrowsAsync<NetworkHoldException>(async () =>
        {
            await using var scope = _provider.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<DownloadService>().ResumeAsync(id);
        });

        Assert.Equal(LeakReason, refusal.Reason);
        // The refusal happened before the engine was touched: nothing was told to resume.
        Assert.DoesNotContain(_engine.NextInfoHash, _engine.Resumed);
        Assert.True((await GetTaskAsync(attemptId))!.IsNetworkHeld);
    }

    [Fact]
    public async Task An_operator_may_override_a_hold_under_this_policy_and_it_is_recorded()
    {
        var attemptId = await ArrangeDownloadingTaskAsync();
        _engine.ObserveUnverified(LeakReason);
        await CheckAsync();
        await CheckAsync();
        var id = new DownloadTaskId((await GetTaskAsync(attemptId))!.Id);

        await using (var scope = _provider.CreateAsyncScope())
        {
            Assert.True(await scope.ServiceProvider.GetRequiredService<DownloadService>()
                .ResumeAsync(id, force: true));
        }

        var task = await GetTaskAsync(attemptId);
        Assert.Equal(DownloadState.Downloading, task!.State);
        Assert.False(task.IsNetworkHeld);
        Assert.Equal("NetworkHoldOverride", task.History[^1].Trigger);
        // And the engine's own session-wide hold was lifted with it: without that the resumed torrent
        // moved nothing inside a held session.
        Assert.Equal(1, _engine.HoldOverrides);
    }

    [Fact]
    public async Task A_released_hold_gives_the_download_a_fresh_stall_clock_on_the_row()
    {
        // Arrange — observed once, then held; the re-establish after the release fails, so only the
        // release itself can restart the clock.
        var attemptId = await ArrangeDownloadingTaskAsync();
        var infoHash = (await GetTaskAsync(attemptId))!.InfoHash!;
        await ApplyStatusAsync(FakeTorrentEngine.Snapshot(infoHash, "downloading", progress: 0.1));
        var before = (await GetTaskAsync(attemptId))!.LastProgressAt;
        _engine.ObserveUnverified(LeakReason);
        await CheckAsync();
        await CheckAsync();
        _engine.FailAdds = true;

        // Act
        _engine.ObserveVerified();
        await CheckAsync();
        await CheckAsync();

        // Assert
        var task = await GetTaskAsync(attemptId);
        Assert.Equal(DownloadState.Downloading, task!.State);
        Assert.True(task.LastProgressAt > before);
    }

    [Fact]
    public async Task No_new_release_is_handed_to_a_held_engine_and_the_refusal_says_why()
    {
        await ArrangeDownloadingTaskAsync();
        _engine.ObserveUnverified(LeakReason);
        await CheckAsync();
        await CheckAsync();

        var addsBefore = _engine.AddedInfoHashes.Count;
        var refusal = await Assert.ThrowsAsync<NetworkHoldException>(async () =>
        {
            await using var scope = _provider.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<DownloadService>().AddDownloadAsync(
                attemptId: Uuid7.New(),
                intentId: Uuid7.New(),
                workId: Uuid7.New(),
                targetId: Uuid7.New(),
                releaseGuid: "g2",
                downloadUrl: "magnet:g2");
        });

        Assert.Equal(LeakReason, refusal.Reason);
        // Nothing reached the engine, so the retry is not spent talking to a process that is held.
        Assert.Equal(addsBefore, _engine.AddedInfoHashes.Count);
        // And it throws rather than returning: a silent success would drop the acquisition with no
        // download, no failure and no record that anything was ever asked for.
        Assert.Contains("retried", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_read_model_reports_the_state_an_operator_needs()
    {
        var attemptId = await ArrangeDownloadingTaskAsync();
        _engine.ObserveUnverified(LeakReason);
        await CheckAsync();
        await CheckAsync();

        await using var scope = _provider.CreateAsyncScope();
        var status = await scope.ServiceProvider.GetRequiredService<IDownloadQuery>().GetTunnelStatusAsync();

        Assert.True(status.Configured);
        Assert.Equal(TunnelLossPolicy.PauseAndAlert, status.Policy);
        Assert.False(status.Verified);
        Assert.Equal(LeakReason, status.Reason);
        Assert.Equal("tun0", status.TunnelDevice);
        Assert.Equal(1, status.HeldTaskCount);
        Assert.NotNull(status.ObservedAt);

        // The same fact reaches the download list, so a held row can be marked without a second call.
        var taskId = (await GetTaskAsync(attemptId))!.Id;
        var active = await scope.ServiceProvider.GetRequiredService<IDownloadQuery>().ListActiveAsync();
        var summary = Assert.Single(active, t => t.Id.Value == taskId);
        Assert.True(summary.NetworkHeld);
    }

    // -- helpers ---------------------------------------------------------------------------------

    private async Task<Guid> ArrangeDownloadingTaskAsync(Guid? targetId = null)
    {
        var target = targetId ?? Uuid7.New();
        await using (var scope = _provider.CreateAsyncScope())
        {
            var commands = scope.ServiceProvider.GetRequiredService<IAcquisitionCommands>();
            await commands.CreateIntentAsync(target, Uuid7.New(), "All");
            await commands.SelectCandidateAsync(Uuid7.New(), target, "g1", "magnet:g1");
        }

        await DrainAsync();
        await ApplyStatusAsync(FakeTorrentEngine.Snapshot(_engine.NextInfoHash, "downloading", progress: 0.4));
        await DrainAsync();

        await using var readScope = _provider.CreateAsyncScope();
        var query = readScope.ServiceProvider.GetRequiredService<IAcquisitionQuery>();
        var intent = await query.GetByTargetAsync(target);
        var detail = await query.GetAsync(intent!.Id.Value);
        return detail!.Attempts.Single().Id.Value;
    }

    private async Task CheckAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<TunnelWatchService>().CheckAsync();
    }

    private async Task ApplyStatusAsync(TorrentSnapshot snapshot)
    {
        await using var scope = _provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DownloadService>()
            .ApplyStatusAsync(snapshot.InfoHash, snapshot);
    }

    private async Task PublishAsync(IDomainEvent domainEvent)
    {
        await using var scope = _provider.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<Operations.Transactions.IUnitOfWork>();
        var bus = scope.ServiceProvider.GetRequiredService<IEventBus>();
        await unitOfWork.ExecuteAsync(token => bus.PublishAsync(domainEvent, token));
    }

    private async Task<DownloadTask?> GetTaskAsync(Guid attemptId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DownloadsDbContext>();
        return await dbContext.Tasks
            .AsNoTracking()
            .Include(t => t.History)
            .FirstOrDefaultAsync(t => t.AttemptId == attemptId);
    }

    private async Task<TunnelStateRecord?> GetStateAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DownloadsDbContext>();
        return await dbContext.TunnelState.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == TunnelStateRecord.SingletonId);
    }

    private async Task<IntentState?> GetIntentStateAsync(Guid targetId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var query = scope.ServiceProvider.GetRequiredService<IAcquisitionQuery>();
        return (await query.GetByTargetAsync(targetId))?.State;
    }

    private async Task<int> CountOutboxAsync(string eventName)
    {
        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<Operations.Persistence.OperationsDbContext>();
        return await dbContext.Outbox.CountAsync(m => m.EventType == eventName);
    }

    private async Task DrainAsync()
    {
        while (true)
        {
            var commands = await DrainCommandsAsync();
            var events = await DrainOutboxAsync();
            if (commands == 0 && events == 0)
            {
                break;
            }
        }
    }

    private async Task<int> DrainCommandsAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var processor = scope.ServiceProvider.GetRequiredService<CommandProcessor>();
        var total = 0;
        int processed;
        while ((processed = await processor.ProcessBatchAsync()) > 0)
        {
            total += processed;
        }

        return total;
    }

    private async Task<int> DrainOutboxAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var relay = scope.ServiceProvider.GetRequiredService<OutboxRelay>();
        var total = 0;
        int published;
        while ((published = await relay.ProcessBatchAsync()) > 0)
        {
            total += published;
        }

        return total;
    }

    private sealed class EgressSink
    {
        public ConcurrentBag<TunnelEgressLost> Lost { get; } = [];

        public ConcurrentBag<TunnelEgressRestored> Restored { get; } = [];

        /// <summary>The transitions whose keyed enqueue was accepted — one per genuine outage.</summary>
        public ConcurrentBag<int> Notified { get; } = [];
    }

    /// <summary>
    /// Stands in for Notifications' handler, which cannot be composed here: it enqueues an idempotent
    /// command keyed by the transition instead of writing under the relay's transaction. The command
    /// type is irrelevant — what is under test is that the second delivery's key is already spent.
    /// </summary>
    private sealed class EgressLostSinkHandler(EgressSink sink, ICommandQueue commandQueue)
        : IEventHandler<TunnelEgressLost>
    {
        public async Task HandleAsync(TunnelEgressLost domainEvent, CancellationToken cancellationToken = default)
        {
            sink.Lost.Add(domainEvent);

            var accepted = await commandQueue.EnqueueAsync(
                new SaveCheckpointsCommand(),
                $"notify:tunnel-egress-lost:{domainEvent.Sequence}",
                cancellationToken);
            if (accepted)
            {
                sink.Notified.Add(domainEvent.Sequence);
            }
        }
    }

    private sealed class EgressRestoredSinkHandler(EgressSink sink) : IEventHandler<TunnelEgressRestored>
    {
        public Task HandleAsync(TunnelEgressRestored domainEvent, CancellationToken cancellationToken = default)
        {
            sink.Restored.Add(domainEvent);
            return Task.CompletedTask;
        }
    }
}

/// <summary>
/// The same guard under <see cref="TunnelLossPolicy.Block"/> and <see cref="TunnelLossPolicy.Ignore"/>,
/// which is where the ordered set of modes is either real or decorative. Its own database, because
/// the host deletes and recreates the one it is given.
/// </summary>
public sealed class TunnelPolicyModeTests : IAsyncLifetime
{
    private readonly FakeTorrentEngine _engine = new();
    private ServiceProvider _provider = null!;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_provider is not null)
        {
            await _provider.DisposeAsync();
        }
    }

    [Fact]
    public async Task Block_holds_on_the_very_first_unverified_observation()
    {
        // Fail closed means the first observation that is not a verification stops the traffic. The
        // cost of being wrong is a pause that lifts on the next poll; the cost of waiting is traffic
        // leaving with the household's address while a counter fills up.
        await ComposeAsync(TunnelLossPolicy.Block);
        var attemptId = await ArrangeDownloadingTaskAsync();
        _engine.ObserveUnverified("tunnel-device-missing");

        await CheckAsync();

        Assert.True((await GetTaskAsync(attemptId))!.IsNetworkHeld);
    }

    [Fact]
    public async Task Block_holds_when_the_sidecar_cannot_be_asked_at_all()
    {
        // Silence is not evidence of safety: an engine that does not answer is the exact condition a
        // kill-switch exists for, and it must not be the one condition it ignores.
        await ComposeAsync(TunnelLossPolicy.Block);
        var attemptId = await ArrangeDownloadingTaskAsync();
        _engine.ObserveUnverified(TunnelObservation.UnreachableReason);

        await CheckAsync();

        var task = await GetTaskAsync(attemptId);
        Assert.True(task!.IsNetworkHeld);
        Assert.Equal(TunnelObservation.UnreachableReason, task.NetworkHoldReason);
    }

    [Fact]
    public async Task Block_holds_when_the_engine_fails_in_a_way_nobody_anticipated()
    {
        // Not every failure arrives as a transport status: an unhandled exception inside the sidecar's
        // handler, a channel that could not be built. An exception escaping the observation would
        // abort the cycle before the streak was touched — no hold, no notification, and a health
        // surface still reporting the last good verdict while nothing at all can be observed.
        await ComposeAsync(TunnelLossPolicy.Block);
        var attemptId = await ArrangeDownloadingTaskAsync();
        _engine.ObserveThrows = new InvalidOperationException("the channel is gone");

        await CheckAsync();

        var task = await GetTaskAsync(attemptId);
        Assert.True(task!.IsNetworkHeld);
        Assert.Equal(TunnelObservation.UnreachableReason, task.NetworkHoldReason);
        Assert.Contains(task.InfoHash!, _engine.Paused);
    }

    [Fact]
    public async Task Block_refuses_the_override_that_PauseAndAlert_allows()
    {
        await ComposeAsync(TunnelLossPolicy.Block);
        var attemptId = await ArrangeDownloadingTaskAsync();
        _engine.ObserveUnverified("tunnel-device-missing");
        await CheckAsync();
        var id = new DownloadTaskId((await GetTaskAsync(attemptId))!.Id);

        await Assert.ThrowsAsync<NetworkHoldException>(async () =>
        {
            await using var scope = _provider.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<DownloadService>().ResumeAsync(id, force: true);
        });

        Assert.True((await GetTaskAsync(attemptId))!.IsNetworkHeld);
        Assert.Equal(0, _engine.HoldOverrides);
    }

    [Fact]
    public async Task Ignore_observes_and_reports_but_holds_nothing()
    {
        await ComposeAsync(TunnelLossPolicy.Ignore);
        var attemptId = await ArrangeDownloadingTaskAsync();
        _engine.ObserveUnverified("egress-identity-not-the-tunnel");

        await CheckAsync();
        await CheckAsync();
        await CheckAsync();

        var task = await GetTaskAsync(attemptId);
        Assert.Equal(DownloadState.Downloading, task!.State);
        Assert.False(task.IsNetworkHeld);

        // Reported, though: the operator asked to see this, they just did not ask Cinomni to act.
        await using var scope = _provider.CreateAsyncScope();
        var status = await scope.ServiceProvider.GetRequiredService<IDownloadQuery>().GetTunnelStatusAsync();
        Assert.False(status.Verified);
        Assert.Equal("egress-identity-not-the-tunnel", status.Reason);
        Assert.Equal(TunnelLossPolicy.Ignore, status.Policy);
        Assert.Equal(0, status.HeldTaskCount);
    }

    [Fact]
    public async Task With_no_tunnel_configured_nothing_is_observed_at_all()
    {
        // The opt-in guarantee, as a test: an installation with no tunnel never calls the sidecar
        // about its egress and never changes a download. This is what keeps local development and
        // the default packaged topology working exactly as they did.
        _provider = await DownloadsTestHost.CreateAsync("cinomni_test_tunnel_off", _engine);
        var attemptId = await ArrangeDownloadingTaskAsync();
        _engine.ObserveUnverified("tunnel-device-missing");

        await CheckAsync();
        await CheckAsync();

        Assert.Equal(0, _engine.TunnelObservations);
        Assert.Equal(DownloadState.Downloading, (await GetTaskAsync(attemptId))!.State);

        await using var scope = _provider.CreateAsyncScope();
        var status = await scope.ServiceProvider.GetRequiredService<IDownloadQuery>().GetTunnelStatusAsync();
        Assert.False(status.Configured);
        Assert.Equal(TunnelObservationReasons.NotYetObserved, status.Reason);
        // Nothing was checked, so nothing is claimed. `Configured: false` is what tells the operator
        // that this does not apply to them; an unobserved `verified: true` would be a lie either way.
        Assert.False(status.Verified);
        Assert.Null(status.ObservedAt);
    }

    private async Task ComposeAsync(TunnelLossPolicy policy) =>
        _provider = await DownloadsTestHost.CreateAsync(
            $"cinomni_test_tunnel_{policy.ToString().ToLowerInvariant()}",
            _engine,
            configureTunnel: tunnel =>
            {
                tunnel.Device = "tun0";
                tunnel.LossPolicy = policy;
            });

    private async Task<Guid> ArrangeDownloadingTaskAsync()
    {
        var target = Uuid7.New();
        await using (var scope = _provider.CreateAsyncScope())
        {
            var commands = scope.ServiceProvider.GetRequiredService<IAcquisitionCommands>();
            await commands.CreateIntentAsync(target, Uuid7.New(), "All");
            await commands.SelectCandidateAsync(Uuid7.New(), target, "g1", "magnet:g1");
        }

        await DrainAsync();
        await using (var scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<DownloadService>().ApplyStatusAsync(
                _engine.NextInfoHash,
                FakeTorrentEngine.Snapshot(_engine.NextInfoHash, "downloading", progress: 0.4));
        }

        await DrainAsync();

        await using var readScope = _provider.CreateAsyncScope();
        var query = readScope.ServiceProvider.GetRequiredService<IAcquisitionQuery>();
        var intent = await query.GetByTargetAsync(target);
        var detail = await query.GetAsync(intent!.Id.Value);
        return detail!.Attempts.Single().Id.Value;
    }

    private async Task CheckAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<TunnelWatchService>().CheckAsync();
    }

    private async Task<DownloadTask?> GetTaskAsync(Guid attemptId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DownloadsDbContext>();
        return await dbContext.Tasks.AsNoTracking().Include(t => t.History)
            .FirstOrDefaultAsync(t => t.AttemptId == attemptId);
    }

    private async Task DrainAsync()
    {
        while (true)
        {
            int commands;
            int events;
            await using (var scope = _provider.CreateAsyncScope())
            {
                var processor = scope.ServiceProvider.GetRequiredService<CommandProcessor>();
                commands = 0;
                int processed;
                while ((processed = await processor.ProcessBatchAsync()) > 0)
                {
                    commands += processed;
                }
            }

            await using (var scope = _provider.CreateAsyncScope())
            {
                var relay = scope.ServiceProvider.GetRequiredService<OutboxRelay>();
                events = 0;
                int published;
                while ((published = await relay.ProcessBatchAsync()) > 0)
                {
                    events += published;
                }
            }

            if (commands == 0 && events == 0)
            {
                return;
            }
        }
    }
}
