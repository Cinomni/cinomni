using System.Collections.Concurrent;
using Cinomni.Acquisition.Contracts;
using Cinomni.Downloads.Contracts;
using Cinomni.Import.Contracts;
using Cinomni.Import.Application;
using Cinomni.Import.Files;
using Cinomni.Import.Persistence;
using Cinomni.Kernel.Identifiers;
using Cinomni.Kernel.Messaging;
using Cinomni.Operations.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Import.Tests;

/// <summary>
/// Integration tests for the import spine against a real PostgreSQL instance: Downloads'
/// DownloadCompleted lands the content (matched, decided, hardlinked, probed, registered) and the
/// feedback (ImportCompleted/ImportFailed) flows back into the acquisition goal. Events are driven by
/// alternately draining the outbox and the command queue, exactly as the hosted services would.
/// </summary>
public sealed class ImportFlowTests : IAsyncLifetime
{
    private const string StagingRoot = "/data/staging";
    private const string ContentPath = "/data/staging/Movie.2024";
    private const string MoviePath = "/data/staging/Movie.2024/Movie.2024.1080p.BluRay.x264.mkv";

    private readonly FakeImportFileSystem _fileSystem = new();
    private readonly FakeMediaProbe _mediaProbe = new();
    private readonly EventSink _sink = new();
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync() =>
        _provider = await ImportTestHost.CreateAsync("cinomni_test_import", _fileSystem, _mediaProbe, services =>
        {
            // The staging root every content path below lives in, as the composition root supplies it.
            services.AddSingleton(new ImportOptions
            {
                LibraryRoot = "/data/test-library",
                StagingRoot = StagingRoot,
                MinVideoBytes = 100,
                MinEpisodeBytes = 100,
            });
            services.AddSingleton(_sink);
            services.AddScoped<IEventHandler<AcquisitionSucceeded>, SucceededSink>();
            services.AddScoped<IEventHandler<MediaAvailable>, MediaAvailableSink>();
        });

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task DownloadCompleted_imports_the_file_and_drives_the_intent_to_available()
    {
        var targetId = Uuid7.New();
        var (intentId, attemptId, workId) = await ArrangeImportingIntentAsync(targetId);
        _fileSystem.SeedContent(ContentPath, new ImportFileEntry(MoviePath, 2000));
        var downloadTaskId = Uuid7.New();

        await DeliverDownloadCompletedAsync(new DownloadCompleted(downloadTaskId, intentId, attemptId, workId, targetId, "infohash", ContentPath));
        await DrainAsync();

        var job = await GetJobAsync(downloadTaskId);
        Assert.NotNull(job);
        Assert.Equal(ImportJobState.Registered, job!.State);
        Assert.NotNull(job.AssetId);
        Assert.NotNull(job.MediaInfo);
        Assert.Equal(2, job.MediaInfo!.Streams.Count);
        Assert.Single(job.Operations);
        Assert.True(job.Operations[0].Verified);

        // The file was hardlinked into the library and probed there.
        Assert.Single(_fileSystem.Linked);
        Assert.Contains(_mediaProbe.Probed, p => p.EndsWith("Movie.2024.1080p.BluRay.x264.mkv"));

        // The goal is met, and it was announced.
        Assert.Equal(IntentState.Available, await GetIntentStateAsync(targetId));
        Assert.Contains(intentId, _sink.Succeeded);
        Assert.Single(_sink.MediaAvailable);
    }

    [Fact]
    public async Task An_unmatched_download_fails_the_import_and_returns_the_goal_to_searching()
    {
        var targetId = Uuid7.New();
        var (intentId, attemptId, workId) = await ArrangeImportingIntentAsync(targetId);
        // Only a junk file: nothing passes the video allowlist.
        _fileSystem.SeedContent(ContentPath, new ImportFileEntry(ContentPath + "/readme.txt", 1000));
        var downloadTaskId = Uuid7.New();
        var completed = new DownloadCompleted(downloadTaskId, intentId, attemptId, workId, targetId, "infohash", ContentPath);

        await DeliverDownloadCompletedAsync(completed);
        await DrainAsync();

        // A redelivery must not re-drive an Unmatched job (it awaits a manual import), and must not throw.
        await DeliverDownloadCompletedAsync(completed);
        await DrainAsync();

        var job = await GetJobAsync(downloadTaskId);
        Assert.Equal(ImportJobState.Unmatched, job!.State);
        // The goal survives an import failure: back to searching (attempts remain), not lost (case #6).
        Assert.Equal(IntentState.Searching, await GetIntentStateAsync(targetId));
    }

    [Theory]
    [InlineData(StagingRoot)]
    [InlineData(StagingRoot + "/")]
    [InlineData("/data/elsewhere/Movie.2024")]
    public async Task A_content_path_that_is_not_a_folder_inside_staging_is_never_scanned(string contentPath)
    {
        // Arrange — the staging root holds another download's film; a completed download points at the
        // root itself (its name sanitised away) or outside it (an edited row).
        var targetId = Uuid7.New();
        var (intentId, attemptId, workId) = await ArrangeImportingIntentAsync(targetId);
        _fileSystem.SeedContent(contentPath, new ImportFileEntry(MoviePath, 2000));
        var downloadTaskId = Uuid7.New();

        // Act
        await DeliverDownloadCompletedAsync(
            new DownloadCompleted(downloadTaskId, intentId, attemptId, workId, targetId, "infohash", contentPath));
        await DrainAsync();

        // Assert — refused before anything was read or linked, and the goal goes back to searching.
        var job = await GetJobAsync(downloadTaskId);
        Assert.Equal(ImportJobState.Unmatched, job!.State);
        Assert.Equal(ImportService.ContentPathNotInStaging, job.Reason);
        Assert.Empty(_fileSystem.Linked);
        Assert.Equal(IntentState.Searching, await GetIntentStateAsync(targetId));
    }

    [Fact]
    public async Task A_probe_failure_still_registers_the_asset_without_streams()
    {
        var targetId = Uuid7.New();
        var (intentId, attemptId, workId) = await ArrangeImportingIntentAsync(targetId);
        _fileSystem.SeedContent(ContentPath, new ImportFileEntry(MoviePath, 2000));
        _mediaProbe.Fail = true;
        var downloadTaskId = Uuid7.New();

        await DeliverDownloadCompletedAsync(new DownloadCompleted(downloadTaskId, intentId, attemptId, workId, targetId, "infohash", ContentPath));
        await DrainAsync();

        var job = await GetJobAsync(downloadTaskId);
        Assert.Equal(ImportJobState.Registered, job!.State);
        Assert.NotNull(job.AssetId);
        Assert.Empty(job.MediaInfo!.Streams);
        Assert.Equal(IntentState.Available, await GetIntentStateAsync(targetId));
    }

    [Fact]
    public async Task A_file_operation_that_fails_to_verify_rolls_back_and_fails_the_import()
    {
        var targetId = Uuid7.New();
        var (intentId, attemptId, workId) = await ArrangeImportingIntentAsync(targetId);
        _fileSystem.SeedContent(ContentPath, new ImportFileEntry(MoviePath, 2000));
        _fileSystem.FailVerify = true; // the hardlink lands nothing, so verify fails
        var downloadTaskId = Uuid7.New();

        await DeliverDownloadCompletedAsync(new DownloadCompleted(downloadTaskId, intentId, attemptId, workId, targetId, "infohash", ContentPath));
        await DrainAsync();

        var job = await GetJobAsync(downloadTaskId);
        // Operation failure returns the job to Pending (rollback), and the operation trail explains it.
        Assert.Equal(ImportJobState.Pending, job!.State);
        Assert.Equal(FileOperationState.RolledBack, job.Operations[0].State);
        Assert.NotEmpty(_fileSystem.Deleted); // the partial result was rolled back
        Assert.Equal(IntentState.Searching, await GetIntentStateAsync(targetId));
    }

    [Fact]
    public async Task Reprocessing_the_same_download_imports_exactly_once()
    {
        var targetId = Uuid7.New();
        var (intentId, attemptId, workId) = await ArrangeImportingIntentAsync(targetId);
        _fileSystem.SeedContent(ContentPath, new ImportFileEntry(MoviePath, 2000));
        var downloadTaskId = Uuid7.New();
        var completed = new DownloadCompleted(downloadTaskId, intentId, attemptId, workId, targetId, "infohash", ContentPath);

        await DeliverDownloadCompletedAsync(completed);
        await DrainAsync();
        await DeliverDownloadCompletedAsync(completed); // redelivery (at-least-once)
        await DrainAsync();

        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ImportDbContext>();
        Assert.Equal(1, await dbContext.Jobs.CountAsync(j => j.DownloadTaskId == downloadTaskId));
        Assert.Single(_fileSystem.Linked); // the terminal job was not re-imported
        Assert.Equal(IntentState.Available, await GetIntentStateAsync(targetId));
    }

    [Fact]
    public async Task Media_available_carries_the_work_id_as_its_single_unit()
    {
        var targetId = Uuid7.New();
        var (intentId, attemptId, workId) = await ArrangeImportingIntentAsync(targetId);
        _fileSystem.SeedContent(ContentPath, new ImportFileEntry(MoviePath, 2000));
        var downloadTaskId = Uuid7.New();

        await DeliverDownloadCompletedAsync(new DownloadCompleted(
            downloadTaskId, intentId, attemptId, workId, targetId, "infohash", ContentPath, UnitIds: [workId]));
        await DrainAsync();

        // A movie is one unit — the work itself — and it stays a single-file, single-asset import.
        var media = Assert.Single(_sink.MediaAvailable);
        Assert.NotNull(media.UnitIds);
        Assert.Equal(workId, Assert.Single(media.UnitIds!));
        Assert.Equal(targetId, Assert.Single(media.TargetIds));
        Assert.Null(media.SeasonNumber);
        Assert.Null(media.EpisodeNumbers);
    }

    [Fact]
    public async Task An_import_without_unit_ids_still_carries_the_work_id()
    {
        var targetId = Uuid7.New();
        var (intentId, attemptId, workId) = await ArrangeImportingIntentAsync(targetId);
        _fileSystem.SeedContent(ContentPath, new ImportFileEntry(MoviePath, 2000));
        var downloadTaskId = Uuid7.New();

        // An in-flight pre-series row carries no unit set; the job falls back to its own work id.
        await DeliverDownloadCompletedAsync(
            new DownloadCompleted(downloadTaskId, intentId, attemptId, workId, targetId, "infohash", ContentPath));
        await DrainAsync();

        var media = Assert.Single(_sink.MediaAvailable);
        Assert.Equal(workId, Assert.Single(media.UnitIds!));
    }

    // -- helpers ---------------------------------------------------------------------------------

    private async Task<(Guid IntentId, Guid AttemptId, Guid WorkId)> ArrangeImportingIntentAsync(Guid targetId)
    {
        var workId = Uuid7.New();
        await using (var scope = _provider.CreateAsyncScope())
        {
            var commands = scope.ServiceProvider.GetRequiredService<IAcquisitionCommands>();
            await commands.CreateIntentAsync(targetId, workId, "All");
            await commands.SelectCandidateAsync(Uuid7.New(), targetId, "g1", "magnet:?xt=urn:btih:g1");
        }

        var (intentId, attemptId) = await ReadIntentAsync(targetId);

        // Advance the goal to Importing, as a completed download would.
        await using (var scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IAcquisitionCommands>().MarkDownloadCompletedAsync(intentId);
        }

        Assert.Equal(IntentState.Importing, await GetIntentStateAsync(targetId));
        return (intentId, attemptId, workId);
    }

    private async Task<(Guid IntentId, Guid AttemptId)> ReadIntentAsync(Guid targetId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var query = scope.ServiceProvider.GetRequiredService<IAcquisitionQuery>();
        var intent = await query.GetByTargetAsync(targetId);
        var detail = await query.GetAsync(intent!.Id.Value);
        return (intent.Id.Value, detail!.Attempts.Single().Id.Value);
    }

    private async Task DeliverDownloadCompletedAsync(DownloadCompleted domainEvent)
    {
        await using var scope = _provider.CreateAsyncScope();
        // The outbox relay fans an event out to every registered consumer; do the same here so both
        // Acquisition (→ Importing, a no-op once already Importing) and Import react.
        foreach (var handler in scope.ServiceProvider.GetServices<IEventHandler<DownloadCompleted>>())
        {
            await handler.HandleAsync(domainEvent);
        }
    }

    private async Task<ImportJob?> GetJobAsync(Guid downloadTaskId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ImportDbContext>();
        return await dbContext.Jobs
            .AsNoTracking()
            .Include(j => j.Operations)
            .Include(j => j.History)
            .FirstOrDefaultAsync(j => j.DownloadTaskId == downloadTaskId);
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

    private sealed class EventSink
    {
        public ConcurrentBag<Guid> Succeeded { get; } = [];

        /// <summary>The events are captured whole so their unit correlation can be asserted.</summary>
        public ConcurrentBag<MediaAvailable> MediaAvailable { get; } = [];
    }

    private sealed class SucceededSink(EventSink sink) : IEventHandler<AcquisitionSucceeded>
    {
        public Task HandleAsync(AcquisitionSucceeded domainEvent, CancellationToken cancellationToken = default)
        {
            sink.Succeeded.Add(domainEvent.IntentId);
            return Task.CompletedTask;
        }
    }

    private sealed class MediaAvailableSink(EventSink sink) : IEventHandler<MediaAvailable>
    {
        public Task HandleAsync(MediaAvailable domainEvent, CancellationToken cancellationToken = default)
        {
            sink.MediaAvailable.Add(domainEvent);
            return Task.CompletedTask;
        }
    }
}
