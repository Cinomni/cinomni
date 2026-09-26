using Cinomni.Downloads;
using Cinomni.Downloads.Contracts;
using Cinomni.Downloads.Messaging;
using Cinomni.Kernel.Identifiers;
using Cinomni.Kernel.Messaging;
using Cinomni.Recovery.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Recovery.Tests;

/// <summary>
/// Validation scenario 13, the startup half — what a restart is allowed to put back on the network.
/// <para>
/// Startup recovery hands every in-flight torrent back to the engine, and it runs before this process
/// has observed anything at all. The verdict it could read on its own is therefore the previous
/// process's last write, or the initial row whose whole point is that "nothing has been checked yet"
/// is not a verification. Trusting the mere absence of a hold is a fail-open at exactly the moment the
/// guard exists to prevent one: a backend that crashed while healthy, and a tunnel that then failed to
/// come up, would have every checkpointed transfer re-armed with no egress guarantee whatsoever.
/// </para>
/// <para>
/// The policy here is <see cref="TunnelLossPolicy.PauseAndAlert"/> on purpose. Under it a single
/// failed observation does <b>not</b> hold, so "is anything held" and "is egress verified" give
/// different answers — which is what makes the distinction assertable rather than incidental.
/// </para>
/// </summary>
[Trait("Scenario", "13")]
public sealed class StartupEgressVerificationTests : IAsyncLifetime
{
    private const string Database = "cinomni_test_recovery_egress_gate";
    private const string ReleaseGuid = "release-egress-gate";
    private const string DownloadUrl = "magnet:?xt=urn:btih:egress-gate";
    private const string TorrentName = "Gated.Feature.2021.1080p.WEB-DL";

    private readonly RecoveryFakes _fakes = new();
    private ServiceProvider _host = null!;
    private RecoveryDriver _driver = null!;

    private Guid _workId;
    private Guid _targetId;
    private Guid _attemptId;

    public async Task InitializeAsync()
    {
        _workId = _fakes.Catalog.SeedMovie("Gated Feature", 2021);
        _targetId = Uuid7.New();
        _fakes.Engine.ScriptRelease(DownloadUrl, TorrentName);

        _fakes.ConfigureTunnel = tunnel =>
        {
            tunnel.Device = "tun0";
            tunnel.LossPolicy = TunnelLossPolicy.PauseAndAlert;
        };

        _host = await RecoveryHost.CreateAsync(Database, _fakes);
        _driver = new RecoveryDriver(_host);

        (_, _attemptId) = await _driver.AcquireAsync(_targetId, _workId, ReleaseGuid, DownloadUrl, [_workId]);
        await _driver.ApplyStatusAsync(_fakes.Engine.SnapshotFor(DownloadUrl, "downloading"));
        await _driver.SaveCheckpointsAsync();

        // The row a healthy process leaves behind: egress verified, nothing held. This is the state the
        // installation is in when it crashes or the host reboots.
        await WatchAsync();
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task A_restart_hands_nothing_back_while_egress_cannot_be_verified_even_though_nothing_is_held()
    {
        // The sidecar answers every call perfectly well and reports that the traffic is not going
        // through the tunnel — the VPN container that failed to come up while the backend was away.
        _fakes.Engine.EgressVerified = false;
        _fakes.Engine.ForgetEverything();
        _fakes.Engine.Adds.Clear();

        await RestartAsync();

        // Not one torrent re-armed, and not because something was holding them: under this policy a
        // single failed observation does not hold, and the last durable verdict still says verified.
        Assert.Empty(_fakes.Engine.Adds);
        Assert.False(_fakes.Engine.Holds(DownloadUrl));

        var task = await _driver.DownloadTaskAsync(_attemptId);
        Assert.False(task!.IsNetworkHeld);
        Assert.DoesNotContain(task.History, line => line.Trigger == "Recover");

        // Nothing is lost by refusing: the checkpoint is exactly where it was.
        Assert.NotNull(task.ResumeData);
        Assert.True(task.CheckpointSeq >= 1);
    }

    [Fact]
    public async Task A_restart_hands_the_transfer_back_once_egress_verifies_in_this_process()
    {
        // The control for the refusal above. Same restart, same absent engine session, same policy —
        // the only difference is that this process took an observation and it verified.
        _fakes.Engine.ForgetEverything();
        _fakes.Engine.Adds.Clear();

        await RestartAsync();

        var readd = Assert.Single(_fakes.Engine.Adds);
        Assert.True(readd.CarriedCheckpoint, "the restart re-added the torrent without its checkpoint");
        Assert.True(_fakes.Engine.Holds(DownloadUrl));
        Assert.Contains(
            (await _driver.DownloadTaskAsync(_attemptId))!.History, line => line.Trigger == "Recover");
    }

    [Fact]
    public async Task An_engine_that_cannot_answer_the_question_is_not_taken_for_a_verification_either()
    {
        // The other way an observation fails to be a verification: it could not be taken at all. Under
        // this policy that is still one failure short of a hold, so again nothing is held and again
        // nothing may be handed back.
        _fakes.Engine.ForgetEverything();
        _fakes.Engine.Adds.Clear();
        _fakes.Engine.Unreachable = true;

        await RestartAsync();
        _fakes.Engine.Unreachable = false;

        Assert.Empty(_fakes.Engine.Adds);
        Assert.False((await _driver.DownloadTaskAsync(_attemptId))!.IsNetworkHeld);
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

    private async Task RestartAsync()
    {
        _host = await RecoveryHost.RestartAsync(_host, Database, _fakes);
        _driver = new RecoveryDriver(_host);
        await _driver.DrainAsync();
    }
}
