using Cinomni.Acquisition.Contracts;
using Cinomni.Downloads;
using Cinomni.Downloads.Application;
using Cinomni.Downloads.Contracts;
using Cinomni.Downloads.Messaging;
using Cinomni.Downloads.Persistence;
using Cinomni.Kernel.Identifiers;
using Cinomni.Kernel.Messaging;
using Cinomni.Operations.Persistence;
using Cinomni.Recovery.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Recovery.Tests;

/// <summary>
/// Validation scenario 13 — torrent egress can no longer be verified.
/// <para>
/// This class verifies the <b>backend's</b> half: what the platform does to its own state when the
/// engine stops answering. The network-layer kill-switch — interface binding, routing, packet
/// filtering inside the sidecar's namespace — is not backend behaviour and is not settled here or by
/// any other test in this repository.
/// </para>
/// <para>
/// The property that matters is that an outage is not a failure. A transfer stopped by the guard
/// keeps its progress and its checkpoint, its acquisition goal is not sent back to searching, and a
/// restart during the outage does not put it back on a network the platform cannot vouch for.
/// </para>
/// </summary>
[Trait("Scenario", "13")]
public sealed class EngineOutageScenarioTests : IAsyncLifetime
{
    private const string Database = "cinomni_test_recovery_egress";
    private const string ReleaseGuid = "release-egress";
    private const string DownloadUrl = "magnet:?xt=urn:btih:egress";
    private const string TorrentName = "Guarded.Feature.2022.1080p.WEB-DL";

    private readonly RecoveryFakes _fakes = new();
    private ServiceProvider _host = null!;
    private RecoveryDriver _driver = null!;

    private Guid _workId;
    private Guid _targetId;
    private Guid _attemptId;

    public async Task InitializeAsync()
    {
        _workId = _fakes.Catalog.SeedMovie("Guarded Feature", 2022);
        _targetId = Uuid7.New();
        _fakes.Engine.ScriptRelease(DownloadUrl, TorrentName);

        // The guard is opt-in, so this host opts in exactly as a deployment does. Block is the default
        // policy and the one worth testing: the first unverified observation is enough.
        _fakes.ConfigureTunnel = tunnel =>
        {
            tunnel.Device = "tun0";
            tunnel.LossPolicy = TunnelLossPolicy.Block;
        };

        _host = await RecoveryHost.CreateAsync(Database, _fakes);
        _driver = new RecoveryDriver(_host);

        (_, _attemptId) = await _driver.AcquireAsync(_targetId, _workId, ReleaseGuid, DownloadUrl, [_workId]);
        await _driver.ApplyStatusAsync(_fakes.Engine.SnapshotFor(DownloadUrl, "downloading"));
        await _driver.SaveCheckpointsAsync();
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task An_engine_that_cannot_be_asked_stops_the_transfer_rather_than_being_taken_for_health()
    {
        _fakes.Engine.Unreachable = true;
        await WatchAsync();

        var task = await _driver.DownloadTaskAsync(_attemptId);
        Assert.Equal(DownloadState.Paused, task!.State);
        Assert.True(task.IsNetworkHeld);
        Assert.Equal("sidecar-unreachable", task.NetworkHoldReason);
    }

    [Fact]
    public async Task Nothing_is_lost_while_the_engine_is_away()
    {
        var before = await _driver.DownloadTaskAsync(_attemptId);
        _fakes.Engine.Unreachable = true;
        await WatchAsync();

        var held = await _driver.DownloadTaskAsync(_attemptId);
        Assert.Equal(before!.ResumeData, held!.ResumeData);
        Assert.Equal(before.CheckpointSeq, held.CheckpointSeq);
        Assert.Equal(before.Progress, held.Progress);

        // A transport outage is not a bad release: sending the goal back to searching would burn
        // indexer quota looking for something it could not fetch either, and lose the transfer with it.
        Assert.Equal(IntentState.Downloading, await _driver.IntentStateAsync(_targetId));
    }

    [Fact]
    public async Task A_restart_during_the_outage_does_not_put_the_transfer_back_on_the_network()
    {
        _fakes.Engine.Unreachable = true;
        await WatchAsync();

        _fakes.Engine.Unreachable = false;
        _fakes.Engine.Adds.Clear();
        await RestartAsync();

        // Recovery and the guard must not fight over the same task. Recovery is the one that gives
        // way: handing a held transfer back to the engine is exactly what the guard exists to prevent.
        Assert.Empty(_fakes.Engine.Adds);
        var held = await _driver.DownloadTaskAsync(_attemptId);
        Assert.True(held!.IsNetworkHeld);
        Assert.DoesNotContain(held.History, line => line.Trigger == "Recover");

        // And the refusal is the hold's doing, not recovery being inert: the same transfer, once the
        // guard lets go of it, is put back on the network — by the release itself, and again,
        // idempotently, by the next start.
        await WatchAsync();
        Assert.False((await _driver.DownloadTaskAsync(_attemptId))!.IsNetworkHeld);
        Assert.Single(_fakes.Engine.Adds);

        _fakes.Engine.Adds.Clear();
        await RestartAsync();

        Assert.Single(_fakes.Engine.Adds);
        Assert.Contains((await _driver.DownloadTaskAsync(_attemptId))!.History, line => line.Trigger == "Recover");
    }

    [Fact]
    public async Task A_sidecar_that_restarted_under_the_hold_is_given_the_transfer_back_when_it_is_released()
    {
        _fakes.Engine.Unreachable = true;
        await WatchAsync();

        // The commonest reason egress cannot be observed is the sidecar process going away, and a
        // sidecar that comes back holds nothing. A release that only resumed the info-hash would
        // succeed against a torrent nobody holds: the row would say Downloading with a valid
        // checkpoint, nothing would transfer, and no timeout and no failure would ever say so.
        _fakes.Engine.ForgetEverything();
        _fakes.Engine.Unreachable = false;
        _fakes.Engine.Adds.Clear();
        Assert.False(_fakes.Engine.Holds(DownloadUrl));

        await WatchAsync();
        await WatchAsync();

        var task = await _driver.DownloadTaskAsync(_attemptId);
        Assert.False(task!.IsNetworkHeld);
        Assert.True(_fakes.Engine.Holds(DownloadUrl));

        // Re-established from the checkpoint the hold preserved, not started from nothing.
        var readd = Assert.Single(_fakes.Engine.Adds);
        Assert.True(readd.CarriedCheckpoint, "the release re-added the torrent without its checkpoint");
        Assert.Contains(task.History, line => line.Trigger == "Recover");

        // And the transfer carries on to its end from there, which is the whole point of not losing it.
        await _driver.ApplyStatusAsync(_fakes.Engine.SnapshotFor(DownloadUrl, "finished", isFinished: true));
        Assert.Equal(DownloadState.Completed, (await _driver.DownloadTaskAsync(_attemptId))!.State);
    }

    [Fact]
    public async Task A_transfer_the_guard_stopped_is_started_again_when_it_is_released()
    {
        // The sidecar never went away this time: the hold paused the torrent it still holds, and a re-add
        // answers with that same paused handle. Re-adding alone would leave the row saying Downloading
        // over a torrent that transfers nothing.
        _fakes.Engine.EgressVerified = false;
        await WatchAsync();
        Assert.Contains(RecoveryTorrentEngine.InfoHashOf(DownloadUrl), _fakes.Engine.Paused);

        _fakes.Engine.EgressVerified = true;
        await WatchAsync();
        await WatchAsync();

        Assert.False((await _driver.DownloadTaskAsync(_attemptId))!.IsNetworkHeld);
        Assert.Contains(RecoveryTorrentEngine.InfoHashOf(DownloadUrl), _fakes.Engine.Resumed);
    }

    [Fact]
    public async Task A_transfer_an_operator_paused_is_not_started_by_recovery()
    {
        await using (var scope = _host.CreateAsyncScope())
        {
            var task = await _driver.DownloadTaskAsync(_attemptId);
            Assert.True(await scope.ServiceProvider.GetRequiredService<DownloadService>().PauseAsync(new DownloadTaskId(task!.Id)));
        }

        _fakes.Engine.ForgetEverything();
        await RestartAsync();

        // Re-added so a later resume has something to start, and stopped again straight away.
        Assert.Contains(RecoveryTorrentEngine.InfoHashOf(DownloadUrl), _fakes.Engine.Paused);
        Assert.DoesNotContain(RecoveryTorrentEngine.InfoHashOf(DownloadUrl), _fakes.Engine.Resumed);
        Assert.Equal(DownloadState.Paused, (await _driver.DownloadTaskAsync(_attemptId))!.State);
    }

    [Fact]
    public async Task A_transfer_the_sidecar_lost_while_the_backend_kept_running_is_given_back()
    {
        // The sidecar was killed and came back (out of memory, a recreated container) while this
        // backend kept running. Nothing restarts here, so startup recovery never runs: the stream only
        // ever answers "unknown", and without the pump noticing, the row says Downloading for good.
        await WatchAsync();
        _fakes.Engine.ForgetEverything();
        _fakes.Engine.Adds.Clear();

        Assert.True(await RecoverLostAsync());

        var readd = Assert.Single(_fakes.Engine.Adds);
        Assert.True(readd.CarriedCheckpoint, "the lost transfer was re-added without its checkpoint");
        Assert.True(_fakes.Engine.Holds(DownloadUrl));
        Assert.Contains(RecoveryTorrentEngine.InfoHashOf(DownloadUrl), _fakes.Engine.Resumed);
        Assert.Contains((await _driver.DownloadTaskAsync(_attemptId))!.History, line => line.Trigger == "Recover");
    }

    [Fact]
    public async Task A_removal_that_lands_while_a_lost_transfer_is_given_back_leaves_nothing_running()
    {
        await WatchAsync();
        _fakes.Engine.ForgetEverything();
        _fakes.Engine.DuringAdd = async () =>
        {
            _fakes.Engine.DuringAdd = null;
            await using var scope = _host.CreateAsyncScope();
            var task = await _driver.DownloadTaskAsync(_attemptId);
            await scope.ServiceProvider.GetRequiredService<DownloadService>()
                .RemoveAsync(new DownloadTaskId(task!.Id), deleteFiles: false);
        };

        // The removal reached the engine before the torrent did, so on its own it removed nothing. A
        // torrent re-added for a task that is now Removed would run with nothing to ever stop it.
        await RecoverLostSwallowingAsync();

        Assert.Equal(DownloadState.Removed, (await _driver.DownloadTaskAsync(_attemptId))!.State);
        Assert.False(_fakes.Engine.Holds(DownloadUrl));
    }

    [Fact]
    public async Task A_pause_that_lands_while_a_lost_transfer_is_given_back_is_not_undone()
    {
        await WatchAsync();
        _fakes.Engine.ForgetEverything();
        _fakes.Engine.DuringAdd = async () =>
        {
            _fakes.Engine.DuringAdd = null;
            await using var scope = _host.CreateAsyncScope();
            var task = await _driver.DownloadTaskAsync(_attemptId);
            await scope.ServiceProvider.GetRequiredService<DownloadService>().PauseAsync(new DownloadTaskId(task!.Id));
        };

        await RecoverLostSwallowingAsync();

        Assert.Equal(DownloadState.Paused, (await _driver.DownloadTaskAsync(_attemptId))!.State);
        Assert.False(_fakes.Engine.IsRunning(DownloadUrl), "an operator's pause was undone by recovery");
    }

    [Fact]
    public async Task A_hold_that_lands_while_a_lost_transfer_is_given_back_is_not_undone()
    {
        await WatchAsync();
        _fakes.Engine.ForgetEverything();
        _fakes.Engine.DuringAdd = async () =>
        {
            _fakes.Engine.DuringAdd = null;
            _fakes.Engine.EgressVerified = false;
            await WatchAsync();
        };

        await RecoverLostSwallowingAsync();

        // The row says held; the engine must agree, or the only guard left when the sidecar's own hold
        // is absent would be a line in the database.
        Assert.True((await _driver.DownloadTaskAsync(_attemptId))!.IsNetworkHeld);
        Assert.False(_fakes.Engine.IsRunning(DownloadUrl), "a hold was undone by recovery");
    }

    [Fact]
    public async Task A_transfer_the_engine_still_holds_is_not_re_added()
    {
        // A stream can end empty for reasons that are not a lost torrent — a refused slot, a dropped
        // connection. The engine is asked before anything is re-added.
        await WatchAsync();
        _fakes.Engine.ScriptStatus(_fakes.Engine.SnapshotFor(DownloadUrl, "downloading"));
        _fakes.Engine.Adds.Clear();

        Assert.False(await RecoverLostAsync());
        Assert.Empty(_fakes.Engine.Adds);
    }

    [Fact]
    public async Task A_lost_transfer_is_not_given_back_while_egress_is_unverified()
    {
        _fakes.Engine.EgressVerified = false;
        await WatchAsync();
        _fakes.Engine.ForgetEverything();
        _fakes.Engine.Adds.Clear();

        // The same rule as startup recovery: nothing goes back on a network nobody can vouch for. The
        // guard's own release re-establishes it once egress verifies again.
        Assert.False(await RecoverLostAsync());
        Assert.Empty(_fakes.Engine.Adds);
    }

    [Fact]
    public async Task The_transfer_is_released_when_egress_is_verified_again()
    {
        _fakes.Engine.Unreachable = true;
        await WatchAsync();

        // Releasing takes the configured streak of verified observations, and holding under Block takes
        // one failure. The asymmetry is the policy's: it is quick to stop and slow to start again.
        _fakes.Engine.Unreachable = false;
        await WatchAsync();
        await WatchAsync();

        var task = await _driver.DownloadTaskAsync(_attemptId);
        Assert.False(task!.IsNetworkHeld);
        Assert.Equal(DownloadState.Downloading, task.State);
        Assert.Contains(task.History, line => line.Trigger == "NetworkRelease");

        // No new attempt was opened along the way: the goal never stopped being the same goal.
        Assert.Single(await _driver.DownloadTasksAsync());
        Assert.Equal(IntentState.Downloading, await _driver.IntentStateAsync(_targetId));
    }

    [Fact]
    public async Task A_release_handed_off_during_a_hold_waits_it_out_without_spending_its_attempts()
    {
        _fakes.Engine.EgressVerified = false;
        await WatchAsync();

        // A second goal selects a release while downloads are held. Retrying the add against the hold
        // would spend its attempts in about a minute and report a perfectly good release as dead.
        var secondTarget = Uuid7.New();
        const string secondUrl = "magnet:?xt=urn:btih:during-the-hold";
        _fakes.Engine.ScriptRelease(secondUrl, "During.The.Hold.2022.1080p.WEB-DL");
        var (_, secondAttempt) = await _driver.AcquireAsync(secondTarget, _workId, "release-held", secondUrl, [_workId]);

        var adds = (await _driver.CommandsAsync(DownloadCommandNames.AddDownload))
            .Where(c => c.IdempotencyKey.Contains(secondAttempt.ToString(), StringComparison.Ordinal))
            .ToList();
        Assert.Contains(adds, c => c.State == CommandState.Completed);
        var waiting = Assert.Single(adds, c => c.State == CommandState.Queued);
        Assert.Equal(0, waiting.Attempts);
        Assert.NotNull(waiting.RunAfter);
        Assert.Equal(IntentState.Downloading, await _driver.IntentStateAsync(secondTarget));

        // Once egress verifies again, the waiting add goes through.
        _fakes.Engine.EgressVerified = true;
        await WatchAsync();
        await WatchAsync();
        await using (var scope = _host.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<OperationsDbContext>().Database
                .ExecuteSqlAsync($"UPDATE operations.command SET run_after = NULL WHERE state = 'Queued'");
        }

        await _driver.DrainAsync();
        Assert.NotNull(await _driver.DownloadTaskAsync(secondAttempt));
    }

    [Fact]
    public async Task The_rest_of_the_installation_keeps_working_while_the_engine_is_down()
    {
        _fakes.Engine.Unreachable = true;
        await WatchAsync();

        // A second goal for a different release. The engine is down, so its download cannot start —
        // but the acquisition itself must still be recorded rather than lost, and the outage must not
        // stop the modules that have nothing to do with the engine from committing.
        var secondTarget = Uuid7.New();
        await using (var scope = _host.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IAcquisitionCommands>()
                .CreateIntentAsync(secondTarget, _workId, "All", _workId);
        }

        await _driver.DrainAsync();
        Assert.Equal(IntentState.Searching, await _driver.IntentStateAsync(secondTarget));
    }

    /// <summary>Runs one egress-watch cycle, which is what the scheduled job does every poll.</summary>
    private async Task WatchAsync()
    {
        await using (var scope = _host.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<CheckTunnelCommand>>();
            Assert.True((await handler.HandleAsync(new CheckTunnelCommand())).IsSuccess);
        }

        await _driver.DrainAsync();
    }

    /// <summary>What the status pump does after a transfer's subscriptions keep ending empty.</summary>
    private async Task<bool> RecoverLostAsync()
    {
        bool recovered;
        await using (var scope = _host.CreateAsyncScope())
        {
            recovered = await scope.ServiceProvider.GetRequiredService<DownloadService>()
                .RecoverLostAsync(RecoveryTorrentEngine.InfoHashOf(DownloadUrl));
        }

        await _driver.DrainAsync();
        return recovered;
    }

    /// <summary>
    /// The same, for the races: the interleaved change can make recovery's own record lose on the
    /// history sequence, which the pump absorbs. What the tests assert is where things end up.
    /// </summary>
    private async Task RecoverLostSwallowingAsync()
    {
        try
        {
            await RecoverLostAsync();
        }
        catch (DbUpdateException)
        {
            // recovery's record lost the race; the engine was still brought back in line
        }
    }

    private async Task RestartAsync()
    {
        _host = await RecoveryHost.RestartAsync(_host, Database, _fakes);
        _driver = new RecoveryDriver(_host);
        await _driver.DrainAsync();
    }
}
