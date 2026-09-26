using Cinomni.Acquisition.Contracts;
using Cinomni.Downloads.Application;
using Cinomni.Downloads.Contracts;
using Cinomni.Downloads.Engine;
using Cinomni.Downloads.Persistence;
using Cinomni.Kernel.Identifiers;
using Cinomni.Operations.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Downloads.Tests;

/// <summary>
/// The limits a transfer has, end to end against PostgreSQL: a control request the state refuses
/// leaves the engine alone, a transfer that stops moving fails and hands its goal back, a finished one
/// stops seeding at its rule, and one the engine lost is closed rather than left seeding for ever.
/// </summary>
public sealed class TransferLimitsTests : IAsyncLifetime
{
    // Short enough for a test to wait out, long enough that two calls in a row never cross it.
    private static readonly TimeSpan StallTimeout = TimeSpan.FromMilliseconds(400);

    private readonly FakeTorrentEngine _engine = new();
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync() =>
        _provider = await DownloadsTestHost.CreateAsync(
            "cinomni_test_downloads_limits",
            _engine,
            configureTransfers: options => options.StallTimeout = StallTimeout);

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task Pausing_a_seeding_download_is_refused_without_touching_the_engine()
    {
        // Arrange
        var (_, attemptId) = await ArrangeDownloadAsync(Uuid7.New(), "g1");
        var infoHash = _engine.NextInfoHash;
        await ApplyStatusAsync(FakeTorrentEngine.Snapshot(infoHash, "seeding", progress: 1.0, isFinished: true));
        var id = new DownloadTaskId((await GetTaskAsync(attemptId))!.Id);

        // Act + Assert
        await Assert.ThrowsAsync<DownloadStateConflictException>(() => ControlAsync(s => s.PauseAsync(id)));
        await Assert.ThrowsAsync<DownloadStateConflictException>(() => ControlAsync(s => s.ResumeAsync(id)));

        Assert.Empty(_engine.Paused);
        Assert.Empty(_engine.Resumed);
        Assert.Equal(DownloadState.Seeding, (await GetTaskAsync(attemptId))!.State);
    }

    [Fact]
    public async Task A_download_that_stops_moving_fails_and_its_goal_searches_again()
    {
        // Arrange — the transfer started and then nothing more arrived.
        var targetId = Uuid7.New();
        var (_, attemptId) = await ArrangeDownloadAsync(targetId, "g1");
        var infoHash = _engine.NextInfoHash;
        await ApplyStatusAsync(FakeTorrentEngine.Snapshot(infoHash, "downloading", progress: 0.1));
        await DrainAsync();

        // Act
        await Task.Delay(StallTimeout * 2);
        await ApplyStatusAsync(FakeTorrentEngine.Snapshot(infoHash, "downloading", progress: 0.1));
        await DrainAsync();

        // Assert
        var task = await GetTaskAsync(attemptId);
        Assert.Equal(DownloadState.Error, task!.State);
        Assert.StartsWith("No progress in", task.LastError, StringComparison.Ordinal);
        Assert.Equal(IntentState.Searching, await GetIntentStateAsync(targetId));
        // And the torrent left the engine: a failed row owns nothing, so left there it would announce,
        // download and seed with no owner.
        Assert.Contains((infoHash, false), _engine.Removed);
    }

    [Fact]
    public async Task A_download_the_engine_failed_is_dropped_from_the_engine_too()
    {
        var (_, attemptId) = await ArrangeDownloadAsync(Uuid7.New(), "g1");
        var infoHash = _engine.NextInfoHash;

        await ApplyStatusAsync(FakeTorrentEngine.Snapshot(infoHash, "downloading", error: "file too short"));
        await ApplyStatusAsync(FakeTorrentEngine.Snapshot(infoHash, "downloading", error: "file too short"));

        Assert.Equal(DownloadState.Error, (await GetTaskAsync(attemptId))!.State);
        Assert.Single(_engine.Removed, r => r == (infoHash, false)); // once, not per snapshot
    }

    [Fact]
    public async Task A_torrent_left_paused_at_the_engine_under_a_running_row_is_not_waiting()
    {
        // A manual pause the row never learned of would otherwise hold the goal for ever.
        var (_, attemptId) = await ArrangeDownloadAsync(Uuid7.New(), "g1");
        var infoHash = _engine.NextInfoHash;
        await ApplyStatusAsync(FakeTorrentEngine.Snapshot(infoHash, "downloading", progress: 0.1));

        await Task.Delay(StallTimeout * 2);
        await ApplyStatusAsync(FakeTorrentEngine.Snapshot(infoHash, "downloading", progress: 0.1, isPaused: true));

        Assert.Equal(DownloadState.Error, (await GetTaskAsync(attemptId))!.State);
    }

    [Fact]
    public async Task A_download_that_keeps_moving_does_not_stall()
    {
        var (_, attemptId) = await ArrangeDownloadAsync(Uuid7.New(), "g1");
        var infoHash = _engine.NextInfoHash;
        await ApplyStatusAsync(FakeTorrentEngine.Snapshot(infoHash, "downloading", progress: 0.1));

        await Task.Delay(StallTimeout * 2);
        await ApplyStatusAsync(FakeTorrentEngine.Snapshot(infoHash, "downloading", progress: 0.2));

        Assert.Equal(DownloadState.Downloading, (await GetTaskAsync(attemptId))!.State);
    }

    [Fact]
    public async Task A_download_the_engine_keeps_queued_does_not_stall()
    {
        var (_, attemptId) = await ArrangeDownloadAsync(Uuid7.New(), "g1");
        var infoHash = _engine.NextInfoHash;
        await ApplyStatusAsync(FakeTorrentEngine.Snapshot(infoHash, "downloading", progress: 0.1));

        await Task.Delay(StallTimeout * 2);
        await ApplyStatusAsync(FakeTorrentEngine.Snapshot(infoHash, "downloading", progress: 0.1, isPaused: true, isQueued: true));

        Assert.Equal(DownloadState.Downloading, (await GetTaskAsync(attemptId))!.State);
    }

    [Fact]
    public async Task A_new_download_stops_seeding_once_it_has_given_back_what_it_took()
    {
        // Arrange
        var (_, attemptId) = await ArrangeDownloadAsync(Uuid7.New(), "g1");
        var infoHash = _engine.NextInfoHash;
        var task = await GetTaskAsync(attemptId);
        Assert.Equal(new TransferOptions().DefaultSeedingPolicy, task!.Policy);
        await ApplyStatusAsync(FakeTorrentEngine.Snapshot(
            infoHash, "seeding", progress: 1.0, isFinished: true, allTimeUpload: 50, allTimeDownload: 100));
        Assert.Equal(DownloadState.Seeding, (await GetTaskAsync(attemptId))!.State);

        // Act
        await ApplyStatusAsync(FakeTorrentEngine.Snapshot(
            infoHash, "seeding", progress: 1.0, isFinished: true, allTimeUpload: 100, allTimeDownload: 100));

        // Assert — removed from the engine, files kept for the library, and the history says why.
        var ended = await GetTaskAsync(attemptId);
        Assert.Equal(DownloadState.Removed, ended!.State);
        Assert.Equal("SeedingDone", ended.History[^1].Trigger);
        Assert.Contains((infoHash, false), _engine.Removed);
    }

    [Fact]
    public async Task A_seeding_download_the_engine_lost_is_closed_instead_of_re_added()
    {
        // Arrange — finished and seeding; then the sidecar restarted and no longer knows the torrent.
        var (_, attemptId) = await ArrangeDownloadAsync(Uuid7.New(), "g1");
        var infoHash = _engine.NextInfoHash;
        _engine.ScriptStream(infoHash, FakeTorrentEngine.Snapshot(infoHash, "seeding", progress: 1.0, isFinished: true));
        await ApplyStatusAsync(FakeTorrentEngine.Snapshot(infoHash, "seeding", progress: 1.0, isFinished: true));
        _engine.Lose(infoHash);
        var adds = _engine.Adds.Count;

        // Act
        await SaveCheckpointsAsync();
        await SaveCheckpointsAsync(); // a second pass finds nothing left to do

        // Assert
        var task = await GetTaskAsync(attemptId);
        Assert.Equal(DownloadState.Removed, task!.State);
        Assert.Single(task.History, h => h.Trigger == "SeedingLost");
        Assert.Equal(adds, _engine.Adds.Count);
        Assert.Empty(_engine.Removed);
    }

    [Fact]
    public async Task A_seeding_download_the_engine_still_holds_keeps_seeding()
    {
        var (_, attemptId) = await ArrangeDownloadAsync(Uuid7.New(), "g1");
        var infoHash = _engine.NextInfoHash;
        _engine.ScriptStream(infoHash, FakeTorrentEngine.Snapshot(infoHash, "seeding", progress: 1.0, isFinished: true));
        await ApplyStatusAsync(FakeTorrentEngine.Snapshot(infoHash, "seeding", progress: 1.0, isFinished: true));

        await SaveCheckpointsAsync();

        Assert.Equal(DownloadState.Seeding, (await GetTaskAsync(attemptId))!.State);
    }

    // -- helpers ---------------------------------------------------------------------------------

    private async Task<(Guid IntentId, Guid AttemptId)> ArrangeDownloadAsync(Guid targetId, string releaseGuid)
    {
        await using (var scope = _provider.CreateAsyncScope())
        {
            var commands = scope.ServiceProvider.GetRequiredService<IAcquisitionCommands>();
            await commands.CreateIntentAsync(targetId, Uuid7.New(), "All");
            await commands.SelectCandidateAsync(Uuid7.New(), targetId, releaseGuid, $"magnet:{releaseGuid}");
        }

        await DrainAsync();

        await using var readScope = _provider.CreateAsyncScope();
        var query = readScope.ServiceProvider.GetRequiredService<IAcquisitionQuery>();
        var intent = await query.GetByTargetAsync(targetId);
        var detail = await query.GetAsync(intent!.Id.Value);
        return (intent.Id.Value, detail!.Attempts.Single().Id.Value);
    }

    private async Task ApplyStatusAsync(TorrentSnapshot snapshot)
    {
        await using var scope = _provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DownloadService>().ApplyStatusAsync(snapshot.InfoHash, snapshot);
    }

    private async Task SaveCheckpointsAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DownloadService>().SaveCheckpointsAsync();
    }

    private async Task<bool> ControlAsync(Func<DownloadService, Task<bool>> action)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<DownloadService>());
    }

    private async Task<DownloadTask?> GetTaskAsync(Guid attemptId)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<DownloadsDbContext>().Tasks
            .AsNoTracking()
            .Include(t => t.History)
            .FirstOrDefaultAsync(t => t.AttemptId == attemptId);
    }

    private async Task<IntentState?> GetIntentStateAsync(Guid targetId)
    {
        await using var scope = _provider.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<IAcquisitionQuery>().GetByTargetAsync(targetId))?.State;
    }

    private async Task DrainAsync()
    {
        while (true)
        {
            int commands;
            int events;
            await using (var scope = _provider.CreateAsyncScope())
            {
                commands = await scope.ServiceProvider.GetRequiredService<CommandProcessor>().ProcessBatchAsync();
            }

            await using (var scope = _provider.CreateAsyncScope())
            {
                events = await scope.ServiceProvider.GetRequiredService<OutboxRelay>().ProcessBatchAsync();
            }

            if (commands == 0 && events == 0)
            {
                break;
            }
        }
    }
}
