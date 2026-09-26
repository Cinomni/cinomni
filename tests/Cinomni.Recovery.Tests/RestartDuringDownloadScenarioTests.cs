using Cinomni.Acquisition.Contracts;
using Cinomni.Downloads.Contracts;
using Cinomni.Downloads.Persistence;
using Cinomni.Import.Files;
using Cinomni.Kernel.Identifiers;
using Cinomni.Recovery.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Recovery.Tests;

/// <summary>
/// Validation scenario 14 — the process restarts while a download is running.
/// <para>
/// The restart is real: the service provider is disposed and a new one is built over the same
/// PostgreSQL database, and the recovery sequence that then runs is <c>Program.cs</c>'s own. What
/// survives is the database and the two things outside the backend that genuinely outlive it — the
/// sidecar and the disk.
/// </para>
/// <para>
/// The claim under test is not "the row is still there". It is that the transfer continues: the
/// engine is handed the torrent back <b>with the checkpoint the platform stored for it</b>, the same
/// task carries on rather than a second one being opened, and the download finishes afterwards and
/// completes its acquisition goal.
/// </para>
/// </summary>
[Trait("Scenario", "14")]
public sealed class RestartDuringDownloadScenarioTests : IAsyncLifetime
{
    private const string Database = "cinomni_test_recovery_download";
    private const string ReleaseGuid = "release-restart-download";
    private const string DownloadUrl = "magnet:?xt=urn:btih:restart-download";
    private const string TorrentName = "Recovered.Feature.2024.1080p.BluRay.x264";

    private readonly RecoveryFakes _fakes = new();
    private ServiceProvider _host = null!;
    private RecoveryDriver _driver = null!;

    private Guid _workId;
    private Guid _targetId;
    private Guid _attemptId;

    public async Task InitializeAsync()
    {
        _workId = _fakes.Catalog.SeedMovie("Recovered Feature", 2024);
        _targetId = Uuid7.New();
        _fakes.Engine.ScriptRelease(DownloadUrl, TorrentName);

        _host = await RecoveryHost.CreateAsync(Database, _fakes);
        _driver = new RecoveryDriver(_host);

        (_, _attemptId) = await _driver.AcquireAsync(_targetId, _workId, ReleaseGuid, DownloadUrl, [_workId]);

        // Half a transfer, and a checkpoint taken over it. Without the checkpoint there is nothing for
        // recovery to carry, and the test would prove only that a row survived a restart.
        await _driver.ApplyStatusAsync(_fakes.Engine.SnapshotFor(DownloadUrl, "downloading"));
        await _driver.SaveCheckpointsAsync();
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task The_checkpoint_the_platform_stored_is_the_one_it_hands_back_to_the_engine()
    {
        var before = await _driver.DownloadTaskAsync(_attemptId);
        Assert.NotNull(before!.ResumeData);
        Assert.True(before.CheckpointSeq >= 1);
        _fakes.Engine.Adds.Clear();

        // The sidecar restarted too, which is the only case in which the checkpoint matters at all.
        _fakes.Engine.ForgetEverything();
        await RestartAsync();

        var readd = Assert.Single(_fakes.Engine.Adds);
        Assert.True(readd.CarriedCheckpoint, "the restart re-added the torrent without its checkpoint");
        Assert.Equal(before.ResumeData, readd.ResumeData);

        // The identity is the checkpoint's, not the URL's — the adapter does not put the download
        // reference on the wire once resume data exists — and it is the same torrent the task was
        // already bound to. A re-add that resolved to a different one would be re-pointing a live
        // task, its claims and its acquisition goal at other content.
        Assert.Equal(before.InfoHash, readd.ResolvedInfoHash);
        Assert.Equal(before.InfoHash, (await _driver.DownloadTaskAsync(_attemptId))!.InfoHash);
    }

    [Fact]
    public async Task A_checkpoint_that_names_another_torrent_does_not_re_point_the_task()
    {
        // Whoever can write downloads.download_tasks.resume_data decides what the engine is handed on
        // this path, because the download URL is not sent once a checkpoint exists: a restored or
        // tampered backup, or an operator-supplied dump, is enough. The blob below is a perfectly
        // valid checkpoint — for a different torrent.
        var before = await _driver.DownloadTaskAsync(_attemptId);
        await ReplaceCheckpointAsync(RecoveryTorrentEngine.CheckpointFor("magnet:?xt=urn:btih:someone-elses"));
        _fakes.Engine.Adds.Clear();
        _fakes.Engine.ForgetEverything();

        await RestartAsync();

        // The task keeps its own identity, its own name and its own state, and records nothing: it is
        // left exactly as it was for an operator to look at rather than silently rebound.
        var after = await _driver.DownloadTaskAsync(_attemptId);
        Assert.Equal(before!.InfoHash, after!.InfoHash);
        Assert.Equal(before.Name, after.Name);
        Assert.Equal(before.State, after.State);
        Assert.DoesNotContain(after.History, line => line.Trigger == "Recover");

        // And the torrent the blob smuggled in is taken back out. Left in the engine it would download
        // and seed with no task behind it — nothing would ever stream, checkpoint or stop it — and
        // with its announce list now kept on a re-add, it would actually find peers.
        Assert.False(_fakes.Engine.Holds("magnet:?xt=urn:btih:someone-elses"));
    }

    [Fact]
    public async Task A_checkpoint_the_engine_cannot_read_does_not_blank_the_task_identity()
    {
        // A corrupt blob makes the engine answer with no identity at all. Writing that answer over the
        // task would blank the key every control call and every status snapshot looks it up by —
        // the download would then be unreachable by anything, for ever.
        var before = await _driver.DownloadTaskAsync(_attemptId);
        await ReplaceCheckpointAsync([0xFF, 0xFE, 0xFD, 0xFC, 0xFB]);
        _fakes.Engine.Adds.Clear();
        _fakes.Engine.ForgetEverything();

        await RestartAsync();

        var after = await _driver.DownloadTaskAsync(_attemptId);
        Assert.Equal(before!.InfoHash, after!.InfoHash);
        Assert.NotNull(after.InfoHash);
        Assert.DoesNotContain(after.History, line => line.Trigger == "Recover");
    }

    [Fact]
    public async Task The_task_records_that_it_was_re_established_and_from_which_checkpoint()
    {
        await RestartAsync();

        var task = await _driver.DownloadTaskAsync(_attemptId);
        var recovered = Assert.Single(task!.History, line => line.Trigger == "Recover");
        Assert.Contains("checkpoint", recovered.Note);

        // A re-add is not a transition: what the transfer is doing is the engine's answer, and it
        // arrives on the next snapshot.
        Assert.Equal(recovered.FromState, recovered.ToState);
        Assert.Equal(DownloadState.Downloading, task.State);
    }

    [Fact]
    public async Task No_second_task_is_opened_for_the_same_attempt()
    {
        // The sidecar went with the backend, so the restart genuinely has to re-add the torrent: a
        // restart that did nothing would satisfy every assertion below without proving any of them.
        _fakes.Engine.ForgetEverything();
        _fakes.Engine.Adds.Clear();

        await RestartAsync();

        Assert.Single(_fakes.Engine.Adds);
        var task = Assert.Single(await _driver.DownloadTasksAsync());
        Assert.Equal(_attemptId, task.AttemptId);
        Assert.Single(task.Claims);
        Assert.Contains(task.History, line => line.Trigger == "Recover");
    }

    [Fact]
    public async Task Restarting_twice_re_establishes_the_same_task_and_creates_nothing_new()
    {
        await RestartAsync();
        var afterOne = await _driver.DownloadTaskAsync(_attemptId);
        _fakes.Engine.Adds.Clear();

        await RestartAsync();

        // One add per restart is correct — the engine is idempotent per info-hash and a re-add of a
        // torrent it already holds changes nothing there. What must not happen is a second task, a
        // second claim, or a checkpoint being lost on the way through.
        Assert.Single(_fakes.Engine.Adds);
        var afterTwo = await _driver.DownloadTaskAsync(_attemptId);
        Assert.Single(await _driver.DownloadTasksAsync());
        Assert.Equal(afterOne!.Id, afterTwo!.Id);
        Assert.Equal(afterOne.CheckpointSeq, afterTwo.CheckpointSeq);
        Assert.Equal(afterOne.ResumeData, afterTwo.ResumeData);
        Assert.Equal(2, afterTwo.History.Count(line => line.Trigger == "Recover"));
    }

    [Fact]
    public async Task The_download_finishes_after_the_restart_and_the_goal_still_completes()
    {
        _fakes.FileSystem.SeedContent(
            $"{RecoveryHost.StagingPath}/{TorrentName}",
            new ImportFileEntry($"{RecoveryHost.StagingPath}/{TorrentName}/{TorrentName}.mkv", 4000));

        // The sidecar went with the backend, so nothing is transferring until recovery says so.
        _fakes.Engine.ForgetEverything();
        Assert.False(_fakes.Engine.Holds(DownloadUrl));

        await RestartAsync();

        // The engine is working on it again. Without this the snapshot below would only be describing
        // a torrent nobody is downloading, and the rest of the assertions would prove nothing.
        Assert.True(_fakes.Engine.Holds(DownloadUrl));

        // The transfer the restart handed back finishes normally, all the way through import.
        await _driver.ApplyStatusAsync(_fakes.Engine.SnapshotFor(DownloadUrl, "finished", isFinished: true));

        var task = await _driver.DownloadTaskAsync(_attemptId);
        Assert.Equal(DownloadState.Completed, task!.State);
        Assert.Equal(IntentState.Available, await _driver.IntentStateAsync(_targetId));

        var asset = Assert.Single(await _driver.AssetsAsync());
        Assert.Equal(_workId, asset.WorkId);
        Assert.Single(_fakes.Events.MediaAvailable);
        Assert.Single(_fakes.Events.ImportCompleted);
    }

    [Fact]
    public async Task A_transfer_the_engine_can_no_longer_be_asked_about_is_left_exactly_as_it_was()
    {
        var before = await _driver.DownloadTaskAsync(_attemptId);
        _fakes.Engine.Adds.Clear();

        // A sidecar that is simply not there. Recovery must not fail the start, must not fail the
        // task, and must not lose the checkpoint it could not use this time.
        _fakes.Engine.Unreachable = true;
        await RestartAsync();
        _fakes.Engine.Unreachable = false;

        Assert.Empty(_fakes.Engine.Adds);
        var after = await _driver.DownloadTaskAsync(_attemptId);
        Assert.Equal(before!.State, after!.State);
        Assert.Equal(before.ResumeData, after.ResumeData);
        Assert.Equal(before.CheckpointSeq, after.CheckpointSeq);
        Assert.DoesNotContain(after.History, line => line.Trigger == "Recover");

        // And the next start picks it up, which is what makes the outage cost a delay and nothing else.
        await RestartAsync();
        Assert.Single(_fakes.Engine.Adds);
        Assert.Contains((await _driver.DownloadTaskAsync(_attemptId))!.History, line => line.Trigger == "Recover");
    }

    /// <summary>
    /// Rewrites the persisted checkpoint, which is what a compromised database, a restored backup or
    /// an operator-supplied dump amounts to. Raw SQL because no code path produces this row.
    /// </summary>
    private async Task ReplaceCheckpointAsync(byte[] resumeData)
    {
        await using var scope = _host.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DownloadsDbContext>();
        var rewritten = await dbContext.Database.ExecuteSqlAsync(
            $"UPDATE downloads.download_tasks SET resume_data = {resumeData}");
        Assert.Equal(1, rewritten);
    }

    private async Task RestartAsync()
    {
        _host = await RecoveryHost.RestartAsync(_host, Database, _fakes);
        _driver = new RecoveryDriver(_host);
        await _driver.DrainAsync();
    }
}
