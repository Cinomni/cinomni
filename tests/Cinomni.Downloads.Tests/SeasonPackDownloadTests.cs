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
/// Integration tests for the season-pack shape of downloading: a task carries every unit it serves,
/// two attempts that picked the same torrent share ONE task, and the completion reports the file
/// layout Downloads has always persisted but never published.
/// <para>
/// The shared-task case is the important one. The status stream subscribes per info-hash and a task
/// is looked up by it, so a second task for the same torrent is never advanced by anything: it stays
/// in <c>Downloading</c> forever, with no timeout, holding its acquisition goal open with it.
/// </para>
/// </summary>
public sealed class SeasonPackDownloadTests : IAsyncLifetime
{
    private readonly FakeTorrentEngine _engine = new();
    private readonly SpineSink _spine = new();
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync() =>
        _provider = await DownloadsTestHost.CreateAsync("cinomni_test_downloads_series", _engine, services =>
        {
            services.AddSingleton(_spine);
            services.AddScoped<IEventHandler<DownloadCompleted>, CompletedSink>();
            services.AddScoped<IEventHandler<DownloadStarted>, StartedSink>();
        });

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task A_pack_download_carries_every_unit_id()
    {
        // Arrange — a season goal whose selected pack covers four episodes.
        var units = NewUnits(4);

        // Act
        await QueueDownloadAsync(Uuid7.New(), Uuid7.New(), "pack", units);
        await DrainAsync();

        // Assert
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DownloadsDbContext>();
        var task = await db.Tasks.Include(t => t.Units).Include(t => t.Claims).SingleAsync();
        Assert.Equal(units.Order(), task.Units.Select(u => u.UnitId).Order());
        var claim = Assert.Single(task.Claims);
        Assert.True(claim.IsOriginating);
    }

    [Fact]
    public async Task Two_attempts_on_the_same_info_hash_create_one_task_carrying_both_unit_sets()
    {
        // Arrange — the collision season packs make routine: a monitored season and a monitored
        // episode inside it independently resolve to the SAME torrent.
        _engine.PinInfoHash = true;
        var seasonUnits = NewUnits(3);
        var episodeUnit = Uuid7.New();

        // Act
        await QueueDownloadAsync(Uuid7.New(), Uuid7.New(), "pack", seasonUnits);
        await DrainAsync();
        await QueueDownloadAsync(Uuid7.New(), Uuid7.New(), "pack", [episodeUnit]);
        await DrainAsync();

        // Assert — one task, both unit sets, two claims.
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DownloadsDbContext>();
        var task = await db.Tasks.Include(t => t.Units).Include(t => t.Claims).SingleAsync();
        Assert.Equal(4, task.Units.Count);
        Assert.Contains(episodeUnit, task.Units.Select(u => u.UnitId));
        Assert.Equal(2, task.Claims.Count);
        Assert.Single(task.Claims, c => c.IsOriginating);
    }

    [Fact]
    public async Task The_listed_summary_names_every_goal_a_shared_task_serves()
    {
        // Arrange — two goals sharing one torrent. The operator's view lists goals, not torrents, so
        // each of them has to be able to find the transfer it is waiting on.
        _engine.PinInfoHash = true;
        var firstIntent = await QueueDownloadAsync(Uuid7.New(), Uuid7.New(), "pack", NewUnits(2));
        await DrainAsync();
        var secondIntent = await QueueDownloadAsync(Uuid7.New(), Uuid7.New(), "pack", NewUnits(1));
        await DrainAsync();

        // Act
        await using var scope = _provider.CreateAsyncScope();
        var tasks = await scope.ServiceProvider.GetRequiredService<IDownloadQuery>().ListActiveAsync();

        // Assert — one task, naming both goals once each, on the wire shape too.
        var task = Assert.Single(tasks);
        Assert.Equal(new[] { firstIntent, secondIntent }.Order(), task.IntentIds.Order());
        Assert.Equal(
            new[] { firstIntent, secondIntent }.Select(id => id.ToString()).Order(),
            DownloadTaskWire.From(task).IntentIds.Order());
    }

    [Fact]
    public async Task Removing_an_unfinished_download_sends_every_goal_it_served_back_to_searching()
    {
        // Arrange — a shared torrent that never got past metadata, the dead-magnet case. Removed
        // without a word to Acquisition, both goals sat in Downloading for ever with nothing behind them.
        _engine.PinInfoHash = true;
        var firstIntent = await QueueDownloadAsync(Uuid7.New(), Uuid7.New(), "pack", NewUnits(2));
        await DrainAsync();
        var secondIntent = await QueueDownloadAsync(Uuid7.New(), Uuid7.New(), "pack", NewUnits(1));
        await DrainAsync();

        // Act
        Assert.True(await RemoveOnlyTaskAsync());
        await DrainAsync();

        // Assert — each goal closed its attempt with the reason and went back to searching.
        await using var scope = _provider.CreateAsyncScope();
        var query = scope.ServiceProvider.GetRequiredService<IAcquisitionQuery>();
        foreach (var intentId in new[] { firstIntent, secondIntent })
        {
            var detail = await query.GetAsync(intentId);
            Assert.NotNull(detail);
            Assert.Equal(IntentState.Searching, detail.Intent.State);
            var attempt = Assert.Single(detail.Attempts);
            Assert.Equal(AttemptState.FailedDownload, attempt.State);
            Assert.Contains("removed", attempt.FailureReason, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Removing_a_finished_download_leaves_its_goal_where_the_completion_put_it()
    {
        // Arrange — the completion was already reported; taking the torrent out afterwards (to stop
        // seeding, say) is not a failed download and must not cost the goal an attempt.
        var infoHash = _engine.NextInfoHash;
        var intentId = await QueueDownloadAsync(Uuid7.New(), Uuid7.New(), "movie", NewUnits(1));
        await DrainAsync();
        await ApplyStatusAsync(FakeTorrentEngine.Snapshot(infoHash, "downloading"));
        await DrainAsync();
        await ApplyStatusAsync(FakeTorrentEngine.Snapshot(infoHash, "seeding", progress: 1.0, isFinished: true));
        await DrainAsync();
        IntentState before;
        await using (var scope = _provider.CreateAsyncScope())
        {
            before = (await scope.ServiceProvider.GetRequiredService<IAcquisitionQuery>().GetAsync(intentId))!.Intent.State;
        }

        // Act
        Assert.True(await RemoveOnlyTaskAsync());
        await DrainAsync();

        // Assert
        await using (var scope = _provider.CreateAsyncScope())
        {
            var detail = await scope.ServiceProvider.GetRequiredService<IAcquisitionQuery>().GetAsync(intentId);
            Assert.NotNull(detail);
            Assert.Equal(before, detail.Intent.State);
            Assert.DoesNotContain(detail.Attempts, a => a.State == AttemptState.FailedDownload);
        }
    }

    [Fact]
    public async Task The_status_stream_advances_the_single_shared_task()
    {
        // Arrange — two goals sharing one torrent.
        _engine.PinInfoHash = true;
        var infoHash = _engine.NextInfoHash;
        var firstIntent = await QueueDownloadAsync(Uuid7.New(), Uuid7.New(), "pack", NewUnits(2));
        await DrainAsync();
        var secondIntent = await QueueDownloadAsync(Uuid7.New(), Uuid7.New(), "pack", NewUnits(1));
        await DrainAsync();

        // Act — one stream, because there is one torrent.
        await ApplyStatusAsync(FakeTorrentEngine.Snapshot(infoHash, "downloading"));
        await DrainAsync();
        await ApplyStatusAsync(FakeTorrentEngine.Snapshot(infoHash, "finished", progress: 1.0, isFinished: true));
        await DrainAsync();

        // Assert — the one task completed, and BOTH goals were told. Before the guard the second
        // goal owned a task nothing ever advanced and stayed in Downloading with no timeout.
        await using (var scope = _provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DownloadsDbContext>();
            var task = await db.Tasks.SingleAsync();
            Assert.Equal(DownloadState.Completed, task.State);
        }

        Assert.Equal(
            new[] { firstIntent, secondIntent }.Order(),
            _spine.Completed.Select(c => c.IntentId).Order());
        Assert.Equal(
            new[] { firstIntent, secondIntent }.Order(),
            _spine.Started.Select(s => s.IntentId).Order());

        // Both goals advanced past Downloading.
        await using (var scope = _provider.CreateAsyncScope())
        {
            var query = scope.ServiceProvider.GetRequiredService<IAcquisitionQuery>();
            var intents = await query.ListAsync();
            Assert.Equal(2, intents.Count);
            Assert.All(intents, i => Assert.Equal(IntentState.Importing, i.State));
        }
    }

    [Fact]
    public async Task Download_completed_publishes_the_file_layout()
    {
        // Arrange — a multi-file pack. ContentPath alone is just a directory, so a consumer that has
        // to map files to episodes needs the layout Downloads already persists.
        var infoHash = _engine.NextInfoHash;
        _engine.SetFiles(
            infoHash,
            new TorrentFileInfo(1, "Pack/S02E02.mkv", 2000, FilePriorityLevel.Normal),
            new TorrentFileInfo(0, "Pack/S02E01.mkv", 1000, FilePriorityLevel.Normal));

        var units = NewUnits(2);
        await QueueDownloadAsync(Uuid7.New(), Uuid7.New(), "pack", units);
        await DrainAsync();

        // Act
        await ApplyStatusAsync(FakeTorrentEngine.Snapshot(infoHash, "downloading"));
        await DrainAsync();
        await ApplyStatusAsync(FakeTorrentEngine.Snapshot(infoHash, "finished", progress: 1.0, isFinished: true));
        await DrainAsync();

        // Assert
        var completed = Assert.Single(_spine.Completed);
        Assert.Equal(units.Order(), completed.UnitIds!.Order());
        Assert.NotNull(completed.Files);
        Assert.Equal([0, 1], completed.Files!.Select(f => f.Index));
        Assert.Equal("Pack/S02E01.mkv", completed.Files[0].RelativePath);
    }

    [Fact]
    public async Task Re_selecting_a_release_the_engine_already_finished_still_reports_back()
    {
        // Arrange — the first goal downloads the pack and keeps seeding it: nothing uploaded and no time
        // seeded yet satisfies no seeding bound, so the engine is never told to remove the torrent and
        // keeps the info-hash. The import then failed, the goal went back to Searching, and six hours
        // later Decision picked the very same release again.
        _engine.PinInfoHash = true;
        var infoHash = _engine.NextInfoHash;
        var firstIntent = await QueueDownloadAsync(Uuid7.New(), Uuid7.New(), "pack", NewUnits(2));
        await DrainAsync();
        await ApplyStatusAsync(FakeTorrentEngine.Snapshot(infoHash, "downloading"));
        await DrainAsync();
        await ApplyStatusAsync(FakeTorrentEngine.Snapshot(infoHash, "seeding", progress: 1.0, isFinished: true));
        await DrainAsync();

        // Act — a brand-new attempt hands off an info-hash the engine already holds as FINISHED, so
        // there is no in-flight task to converge on and the status stream only ever reports finished.
        var secondIntent = await QueueDownloadAsync(Uuid7.New(), Uuid7.New(), "pack", NewUnits(2));
        await DrainAsync();
        await ApplyStatusAsync(FakeTorrentEngine.Snapshot(infoHash, "seeding", progress: 1.0, isFinished: true));
        await DrainAsync();

        // Assert — the second goal was told its download finished. Without the transition it sat in
        // Downloading forever: no DownloadCompleted, no DownloadFailed, no timeout, and every later
        // candidate refused because the goal was not searchable.
        Assert.Equal(
            new[] { firstIntent, secondIntent }.Order(),
            _spine.Completed.Select(c => c.IntentId).Order());

        await using (var scope = _provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DownloadsDbContext>();
            var newest = await db.Tasks.OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id).FirstAsync();
            Assert.Equal(DownloadState.Seeding, newest.State);
            Assert.NotNull(newest.ContentPath);
        }

        await using (var scope = _provider.CreateAsyncScope())
        {
            var query = scope.ServiceProvider.GetRequiredService<IAcquisitionQuery>();
            var second = Assert.Single(await query.ListAsync(), i => i.Id.Value == secondIntent);
            Assert.Equal(IntentState.Importing, second.State);
        }
    }

    [Fact]
    public async Task Two_different_torrents_still_get_their_own_tasks()
    {
        // The guard must not over-reach: distinct releases are distinct downloads.
        await QueueDownloadAsync(Uuid7.New(), Uuid7.New(), "first", NewUnits(1));
        await DrainAsync();
        await QueueDownloadAsync(Uuid7.New(), Uuid7.New(), "second", NewUnits(1));
        await DrainAsync();

        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DownloadsDbContext>();
        Assert.Equal(2, await db.Tasks.CountAsync());
        Assert.Equal(2, _engine.AddedInfoHashes.Distinct().Count());
    }

    // -- helpers ---------------------------------------------------------------------------------

    private static List<Guid> NewUnits(int count) =>
        Enumerable.Range(0, count).Select(_ => Uuid7.New()).ToList();

    /// <summary>
    /// Drives the real spine: a goal, a selection carrying units, then DownloadQueued. Returns the
    /// goal's id, which is what the download feedback is routed by.
    /// </summary>
    private async Task<Guid> QueueDownloadAsync(
        Guid targetId,
        Guid workId,
        string releaseGuid,
        IReadOnlyList<Guid> unitIds)
    {
        await using var scope = _provider.CreateAsyncScope();
        var commands = scope.ServiceProvider.GetRequiredService<IAcquisitionCommands>();
        var intentId = await commands.CreateIntentAsync(targetId, workId, "All");
        await commands.SelectCandidateAsync(Uuid7.New(), targetId, releaseGuid, $"magnet:{releaseGuid}", unitIds);
        return intentId.Value;
    }

    private async Task<bool> RemoveOnlyTaskAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DownloadsDbContext>();
        var id = await db.Tasks.Select(t => t.Id).SingleAsync();
        return await scope.ServiceProvider.GetRequiredService<DownloadService>()
            .RemoveAsync(new DownloadTaskId(id), deleteFiles: false);
    }

    private async Task ApplyStatusAsync(TorrentSnapshot snapshot)
    {
        await using var scope = _provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<DownloadService>();
        await service.ApplyStatusAsync(snapshot.InfoHash, snapshot);
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

    private sealed class SpineSink
    {
        public ConcurrentBag<DownloadCompleted> Completed { get; } = [];

        public ConcurrentBag<DownloadStarted> Started { get; } = [];
    }

    private sealed class CompletedSink(SpineSink sink) : IEventHandler<DownloadCompleted>
    {
        public Task HandleAsync(DownloadCompleted domainEvent, CancellationToken cancellationToken = default)
        {
            sink.Completed.Add(domainEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class StartedSink(SpineSink sink) : IEventHandler<DownloadStarted>
    {
        public Task HandleAsync(DownloadStarted domainEvent, CancellationToken cancellationToken = default)
        {
            sink.Started.Add(domainEvent);
            return Task.CompletedTask;
        }
    }
}
