using System.Collections.Concurrent;
using Cinomni.Acquisition.Contracts;
using Cinomni.Downloads.Application;
using Cinomni.Downloads.Contracts;
using Cinomni.Downloads.Engine;
using Cinomni.Downloads.Persistence;
using Cinomni.Kernel.Identifiers;
using Cinomni.Kernel.Messaging;
using Cinomni.Operations.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Downloads.Tests;

/// <summary>
/// Integration tests for the download spine against a real PostgreSQL instance: Acquisition's
/// DownloadQueued adds a task, the (faked) sidecar status stream advances it, and the feedback
/// (DownloadStarted/Completed/Failed) flows back into the acquisition goal. Events are driven by
/// alternately draining the outbox and the command queue, exactly as the hosted services would.
/// </summary>
public sealed class DownloadFlowTests : IAsyncLifetime
{
    private readonly FakeTorrentEngine _engine = new();
    private readonly StateSavedSink _sink = new();
    private readonly SpineSink _spine = new();
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync() =>
        _provider = await DownloadsTestHost.CreateAsync("cinomni_test_downloads", _engine, services =>
        {
            services.AddSingleton(_sink);
            services.AddSingleton(_spine);
            services.AddScoped<IEventHandler<DownloadStateSaved>, StateSavedSinkHandler>();
            services.AddScoped<IEventHandler<DownloadQueued>, QueuedSinkHandler>();
            services.AddScoped<IEventHandler<DownloadCompleted>, CompletedSinkHandler>();
        });

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task DownloadQueued_creates_a_queued_task_with_acquisition_correlation()
    {
        var targetId = Uuid7.New();
        var (intentId, attemptId) = await ArrangeDownloadingIntentAsync(targetId, "g1", "magnet:?xt=urn:btih:g1");

        var task = await GetTaskAsync(attemptId);
        Assert.NotNull(task);
        Assert.Equal(DownloadState.Queued, task!.State);
        Assert.Equal(intentId, task.IntentId);
        Assert.Equal(_engine.NextInfoHash, task.InfoHash);
    }

    [Fact]
    public async Task Stream_downloading_then_finished_drives_the_intent_to_importing()
    {
        var targetId = Uuid7.New();
        var (_, attemptId) = await ArrangeDownloadingIntentAsync(targetId, "g1", "magnet:g1");
        var infoHash = _engine.NextInfoHash;

        await ApplyStatusAsync(FakeTorrentEngine.Snapshot(infoHash, "downloading", progress: 0.5));
        await DrainAsync();

        await ApplyStatusAsync(FakeTorrentEngine.Snapshot(infoHash, "finished", progress: 1.0, isFinished: true));
        await DrainAsync();

        var task = await GetTaskAsync(attemptId);
        Assert.Equal(DownloadState.Completed, task!.State);
        Assert.Equal(IntentState.Importing, await GetIntentStateAsync(targetId));
    }

    [Fact]
    public async Task A_magnet_dn_is_replaced_by_the_torrent_name_once_the_metadata_resolves()
    {
        // Arrange — the add answers with the magnet's dn= and no layout, the way an engine that
        // reports a name before the metadata does. The torrent writes a differently named folder.
        _engine.NextName = "Label.The.Indexer.Chose";
        var targetId = Uuid7.New();
        var (_, attemptId) = await ArrangeDownloadingIntentAsync(targetId, "g1", "magnet:?xt=urn:btih:g1&dn=Label.The.Indexer.Chose");
        var infoHash = _engine.NextInfoHash;
        _engine.SetFiles(infoHash, new TorrentFileInfo(0, "Real.Folder/movie.mkv", 1000, FilePriorityLevel.Normal));

        // Act — the metadata resolves; later a snapshot reports another name once the layout is known.
        await ApplyStatusAsync(FakeTorrentEngine.Snapshot(infoHash, "downloading", name: "Real.Folder", progress: 0.5));
        await ApplyStatusAsync(FakeTorrentEngine.Snapshot(infoHash, "downloading", name: "Renamed.Later", progress: 0.7));
        await ApplyStatusAsync(FakeTorrentEngine.Snapshot(infoHash, "finished", name: "Renamed.Later", progress: 1.0, isFinished: true));
        await DrainAsync();

        // Assert — Import is sent to the folder the torrent actually wrote, and a recorded layout pins the name.
        var task = await GetTaskAsync(attemptId);
        Assert.Equal("Real.Folder", task!.Name);
        Assert.Single(task.Files);
        var completed = Assert.Single(_spine.Completed, c => c.AttemptId == attemptId);
        Assert.Equal($"/data/test-downloads/{attemptId}/Real.Folder", completed.ContentPath);
    }

    [Fact]
    public async Task A_torrent_whose_name_sanitises_to_nothing_fails_instead_of_importing_the_staging_root()
    {
        // Arrange — a swarm-supplied name that survives the engine but not the single-segment rule.
        _engine.NextName = string.Empty;
        var targetId = Uuid7.New();
        var (_, attemptId) = await ArrangeDownloadingIntentAsync(targetId, "g1", "magnet:?xt=urn:btih:g1");
        var infoHash = _engine.NextInfoHash;
        _engine.SetFiles(infoHash, new TorrentFileInfo(0, ".../movie.mkv", 1000, FilePriorityLevel.Normal));

        // Act
        await ApplyStatusAsync(FakeTorrentEngine.Snapshot(infoHash, "downloading", name: "...", progress: 0.5));
        await ApplyStatusAsync(FakeTorrentEngine.Snapshot(infoHash, "finished", name: "...", progress: 1.0, isFinished: true));
        await DrainAsync();

        // Assert — Import is never pointed at the directory every download shares.
        var task = await GetTaskAsync(attemptId);
        Assert.Equal(DownloadState.Error, task!.State);
        Assert.Null(task.ContentPath);
        Assert.DoesNotContain(_spine.Completed, c => c.AttemptId == attemptId);
        Assert.Equal(IntentState.Searching, await GetIntentStateAsync(targetId));
    }

    [Fact]
    public async Task Stream_error_returns_the_intent_to_searching()
    {
        var targetId = Uuid7.New();
        var (_, attemptId) = await ArrangeDownloadingIntentAsync(targetId, "g1", "magnet:g1");
        var infoHash = _engine.NextInfoHash;

        await ApplyStatusAsync(FakeTorrentEngine.Snapshot(infoHash, "downloading"));
        await DrainAsync();
        await ApplyStatusAsync(FakeTorrentEngine.Snapshot(infoHash, "downloading", error: "tracker failure"));
        await DrainAsync();

        var task = await GetTaskAsync(attemptId);
        Assert.Equal(DownloadState.Error, task!.State);
        // The goal survives a failure: back to searching (attempts remain), not lost (case #6).
        Assert.Equal(IntentState.Searching, await GetIntentStateAsync(targetId));
    }

    [Fact]
    public async Task Checkpoint_persists_resume_data_and_emits_state_saved()
    {
        var targetId = Uuid7.New();
        var (_, attemptId) = await ArrangeDownloadingIntentAsync(targetId, "g1", "magnet:g1");
        var infoHash = _engine.NextInfoHash;
        _engine.ScriptStream(infoHash, FakeTorrentEngine.Snapshot(infoHash, "downloading", progress: 0.5));
        await ApplyStatusAsync(FakeTorrentEngine.Snapshot(infoHash, "downloading", progress: 0.5));
        await DrainAsync();

        await SaveCheckpointsAsync();
        await DrainAsync();

        var task = await GetTaskAsync(attemptId);
        Assert.NotNull(task!.ResumeData);
        Assert.True(task.CheckpointSeq >= 1);
        Assert.Contains(task.Id, _sink.Saved);
    }

    [Fact]
    public async Task Every_task_downloads_into_a_folder_of_its_own()
    {
        // Two releases that name their folder alike used to share one directory, so what a failed one
        // left behind could be imported as the other.
        _engine.NextName = "Same.Name.2024";
        var (_, firstAttempt) = await ArrangeDownloadingIntentAsync(Uuid7.New(), "g1", "magnet:g1");
        var (_, secondAttempt) = await ArrangeDownloadingIntentAsync(Uuid7.New(), "g2", "magnet:g2");

        var first = await GetTaskAsync(firstAttempt);
        var second = await GetTaskAsync(secondAttempt);

        Assert.Equal($"/data/test-downloads/{firstAttempt}", first!.SavePath);
        Assert.Equal($"/data/test-downloads/{secondAttempt}", second!.SavePath);
        Assert.All(_engine.Adds, add => Assert.Equal("/data/test-downloads", add.StagingRoot));
        Assert.Contains(_engine.Adds, add => add.SavePath == first.SavePath);
        Assert.Contains(_engine.Adds, add => add.SavePath == second.SavePath);
    }

    [Fact]
    public async Task A_retried_add_names_the_folder_the_engine_already_writes_to()
    {
        // Arrange — the engine takes the torrent, then the add fails before the task is written; the
        // command is retried and the engine answers with the torrent it already holds.
        var attemptId = Uuid7.New();
        _engine.PinInfoHash = true;
        _engine.FailNextListings = 1;

        // Act
        await using (var scope = _provider.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<DownloadService>();
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.AddDownloadAsync(
                attemptId, Uuid7.New(), Uuid7.New(), Uuid7.New(), "g1", "magnet:g1"));
        }

        await using (var scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<DownloadService>().AddDownloadAsync(
                attemptId, Uuid7.New(), Uuid7.New(), Uuid7.New(), "g1", "magnet:g1");
        }

        // Assert — both adds named one folder, and the task records it: a fresh one per try would
        // leave the task reading an empty folder while the torrent fills the first.
        var adds = _engine.Adds.ToList();
        Assert.Equal(2, adds.Count);
        Assert.Equal(adds[0].SavePath, adds[1].SavePath);
        Assert.Equal(adds[0].SavePath, (await GetTaskAsync(attemptId))!.SavePath);
    }

    [Fact]
    public async Task A_torrent_the_engine_still_seeds_is_followed_to_the_folder_it_writes()
    {
        // Arrange — the first task finished and seeds; the same release is grabbed again elsewhere.
        _engine.PinInfoHash = true;
        var (_, firstAttempt) = await ArrangeDownloadingIntentAsync(Uuid7.New(), "g1", "magnet:g1");
        var infoHash = _engine.NextInfoHash;
        await ApplyStatusAsync(FakeTorrentEngine.Snapshot(infoHash, "seeding", progress: 1.0, isFinished: true));
        await DrainAsync();
        var first = await GetTaskAsync(firstAttempt);
        Assert.Equal(DownloadState.Seeding, first!.State);

        // Act
        var (_, secondAttempt) = await ArrangeDownloadingIntentAsync(Uuid7.New(), "g1", "magnet:g1");

        // Assert — the engine answered with the torrent it already holds, so the new task reads from
        // where that torrent writes rather than from a fresh, empty folder.
        var second = await GetTaskAsync(secondAttempt);
        Assert.NotEqual(first.Id, second!.Id);
        Assert.Equal(first.SavePath, second.SavePath);
    }

    [Fact]
    public async Task One_task_that_cannot_be_checkpointed_does_not_cost_the_others_theirs()
    {
        // Arrange — two downloads in flight; the engine cannot hand over the first one's resume data.
        var (_, firstAttempt) = await ArrangeDownloadingIntentAsync(Uuid7.New(), "g1", "magnet:g1");
        var (_, secondAttempt) = await ArrangeDownloadingIntentAsync(Uuid7.New(), "g2", "magnet:g2");
        var first = await GetTaskAsync(firstAttempt);
        var second = await GetTaskAsync(secondAttempt);
        _engine.FailResumeFor.Add(first!.InfoHash!);

        // Act
        await SaveCheckpointsAsync();

        // Assert — the pass went on past the failure.
        Assert.Null((await GetTaskAsync(firstAttempt))!.ResumeData);
        Assert.NotNull((await GetTaskAsync(secondAttempt))!.ResumeData);
        Assert.NotEqual(first.InfoHash, second!.InfoHash);
    }

    [Fact]
    public async Task Pause_then_resume_updates_state_and_calls_the_engine()
    {
        var targetId = Uuid7.New();
        var (_, attemptId) = await ArrangeDownloadingIntentAsync(targetId, "g1", "magnet:g1");
        var infoHash = _engine.NextInfoHash;
        await ApplyStatusAsync(FakeTorrentEngine.Snapshot(infoHash, "downloading"));
        await DrainAsync();

        var id = new DownloadTaskId((await GetTaskAsync(attemptId))!.Id);

        Assert.True(await ControlAsync(service => service.PauseAsync(id)));
        Assert.Equal(DownloadState.Paused, (await GetTaskAsync(attemptId))!.State);
        Assert.Contains(infoHash, _engine.Paused);

        Assert.True(await ControlAsync(service => service.ResumeAsync(id)));
        Assert.Equal(DownloadState.Downloading, (await GetTaskAsync(attemptId))!.State);
        Assert.Contains(infoHash, _engine.Resumed);
    }

    [Fact]
    public async Task Adding_the_same_attempt_twice_creates_one_task()
    {
        var targetId = Uuid7.New();
        var (intentId, attemptId) = await ArrangeDownloadingIntentAsync(targetId, "g1", "magnet:g1");

        await using (var scope = _provider.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<DownloadService>();
            await service.AddDownloadAsync(attemptId, intentId, Guid.NewGuid(), Guid.NewGuid(), "g1", "magnet:g1");
        }

        await using var readScope = _provider.CreateAsyncScope();
        var dbContext = readScope.ServiceProvider.GetRequiredService<DownloadsDbContext>();
        Assert.Equal(1, await dbContext.Tasks.CountAsync(t => t.AttemptId == attemptId));
    }

    [Fact]
    public async Task Set_file_priorities_updates_the_files_and_calls_the_engine()
    {
        var targetId = Uuid7.New();
        var infoHash = _engine.NextInfoHash;
        _engine.SetFiles(
            infoHash,
            new TorrentFileInfo(0, "movie.mkv", 1000, FilePriorityLevel.Normal),
            new TorrentFileInfo(1, "sample.mkv", 50, FilePriorityLevel.Normal));
        var (_, attemptId) = await ArrangeDownloadingIntentAsync(targetId, "g1", "magnet:g1");

        var id = new DownloadTaskId((await GetTaskAsync(attemptId))!.Id);
        await ControlAsync(service =>
            service.SetFilePrioritiesAsync(id, new Dictionary<int, FilePriorityLevel> { [1] = FilePriorityLevel.Skip }));

        var task = await GetTaskAsync(attemptId);
        Assert.Equal(FilePriorityLevel.Skip, task!.Files.Single(f => f.Index == 1).Priority);
        Assert.True(_engine.PrioritiesSet.ContainsKey(infoHash));
    }

    [Fact]
    public async Task Download_queued_echoes_the_unit_ids()
    {
        var targetId = Uuid7.New();
        var workId = Uuid7.New();

        await using (var scope = _provider.CreateAsyncScope())
        {
            var commands = scope.ServiceProvider.GetRequiredService<IAcquisitionCommands>();
            await commands.CreateIntentAsync(targetId, workId, "All");
            await commands.SelectCandidateAsync(Uuid7.New(), targetId, "g1", "magnet:g1");
        }

        await DrainAsync();

        // For a movie the acquired unit is the work itself, so the correlation is [workId].
        var queued = Assert.Single(_spine.Queued);
        Assert.NotNull(queued.UnitIds);
        Assert.Equal(workId, Assert.Single(queued.UnitIds!));
    }

    [Fact]
    public async Task Download_completed_carries_the_unit_ids_and_the_torrent_file_layout()
    {
        var targetId = Uuid7.New();
        var workId = Uuid7.New();
        var infoHash = _engine.NextInfoHash;
        _engine.SetFiles(
            infoHash,
            new TorrentFileInfo(1, "Pack/second.mkv", 2000, FilePriorityLevel.Normal),
            new TorrentFileInfo(0, "Pack/first.mkv", 1000, FilePriorityLevel.Normal));

        await using (var scope = _provider.CreateAsyncScope())
        {
            var commands = scope.ServiceProvider.GetRequiredService<IAcquisitionCommands>();
            await commands.CreateIntentAsync(targetId, workId, "All");
            await commands.SelectCandidateAsync(Uuid7.New(), targetId, "g1", "magnet:g1");
        }

        await DrainAsync();
        await ApplyStatusAsync(FakeTorrentEngine.Snapshot(infoHash, "downloading"));
        await DrainAsync();
        await ApplyStatusAsync(FakeTorrentEngine.Snapshot(infoHash, "finished", progress: 1.0, isFinished: true));
        await DrainAsync();

        var completed = Assert.Single(_spine.Completed);
        Assert.Equal(workId, Assert.Single(completed.UnitIds!));
        Assert.NotNull(completed.Files);
        Assert.Equal(2, completed.Files!.Count);
        // The layout is published in engine file order, not persistence order.
        Assert.Equal([0, 1], completed.Files.Select(f => f.Index));
        Assert.Equal("Pack/first.mkv", completed.Files[0].RelativePath);
        Assert.Equal(2000, completed.Files[1].Size);
    }

    // -- helpers ---------------------------------------------------------------------------------

    private async Task<(Guid IntentId, Guid AttemptId)> ArrangeDownloadingIntentAsync(
        Guid targetId,
        string releaseGuid,
        string downloadUrl)
    {
        await using (var scope = _provider.CreateAsyncScope())
        {
            var commands = scope.ServiceProvider.GetRequiredService<IAcquisitionCommands>();
            await commands.CreateIntentAsync(targetId, Uuid7.New(), "All");
            await commands.SelectCandidateAsync(Uuid7.New(), targetId, releaseGuid, downloadUrl);
        }

        await DrainAsync(); // DownloadQueued → AddDownload → task created

        await using var readScope = _provider.CreateAsyncScope();
        var query = readScope.ServiceProvider.GetRequiredService<IAcquisitionQuery>();
        var intent = await query.GetByTargetAsync(targetId);
        var detail = await query.GetAsync(intent!.Id.Value);
        var attempt = detail!.Attempts.Single();
        return (intent.Id.Value, attempt.Id.Value);
    }

    private async Task ApplyStatusAsync(TorrentSnapshot snapshot)
    {
        await using var scope = _provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<DownloadService>();
        await service.ApplyStatusAsync(snapshot.InfoHash, snapshot);
    }

    private async Task SaveCheckpointsAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<DownloadService>();
        await service.SaveCheckpointsAsync();
    }

    private async Task<bool> ControlAsync(Func<DownloadService, Task<bool>> action)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<DownloadService>());
    }

    private async Task<DownloadTask?> GetTaskAsync(Guid attemptId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DownloadsDbContext>();
        return await dbContext.Tasks
            .AsNoTracking()
            .Include(t => t.Files)
            .Include(t => t.History)
            .FirstOrDefaultAsync(t => t.AttemptId == attemptId);
    }

    private async Task<IntentState?> GetIntentStateAsync(Guid targetId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var query = scope.ServiceProvider.GetRequiredService<IAcquisitionQuery>();
        var intent = await query.GetByTargetAsync(targetId);
        return intent?.State;
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

    private sealed class StateSavedSink
    {
        public ConcurrentBag<Guid> Saved { get; } = [];
    }

    private sealed class StateSavedSinkHandler(StateSavedSink sink) : IEventHandler<DownloadStateSaved>
    {
        public Task HandleAsync(DownloadStateSaved domainEvent, CancellationToken cancellationToken = default)
        {
            sink.Saved.Add(domainEvent.DownloadTaskId);
            return Task.CompletedTask;
        }
    }

    /// <summary>Captures the spine events whole, so their unit correlation can be asserted.</summary>
    private sealed class SpineSink
    {
        public ConcurrentBag<DownloadQueued> Queued { get; } = [];

        public ConcurrentBag<DownloadCompleted> Completed { get; } = [];
    }

    private sealed class QueuedSinkHandler(SpineSink sink) : IEventHandler<DownloadQueued>
    {
        public Task HandleAsync(DownloadQueued domainEvent, CancellationToken cancellationToken = default)
        {
            sink.Queued.Add(domainEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class CompletedSinkHandler(SpineSink sink) : IEventHandler<DownloadCompleted>
    {
        public Task HandleAsync(DownloadCompleted domainEvent, CancellationToken cancellationToken = default)
        {
            sink.Completed.Add(domainEvent);
            return Task.CompletedTask;
        }
    }
}
