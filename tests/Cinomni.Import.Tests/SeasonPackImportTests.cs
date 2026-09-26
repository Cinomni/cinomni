using System.Collections.Concurrent;
using Cinomni.Acquisition.Contracts;
using Cinomni.Downloads.Contracts;
using Cinomni.Import.Contracts;
using Cinomni.Import.Files;
using Cinomni.Import.Persistence;
using Cinomni.Kernel.Identifiers;
using Cinomni.Kernel.Messaging;
using Cinomni.Operations.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Import.Tests;

/// <summary>
/// Integration tests for a season-pack import against a real PostgreSQL instance: one download of
/// many episode files lands N assets, publishes one MediaAvailable per file carrying only that
/// file's own units, and exactly one ImportCompleted for the whole job.
/// </summary>
public sealed class SeasonPackImportTests : IAsyncLifetime
{
    private const string ContentPath = "/data/staging/The.Wire.S01.1080p.BluRay.x264";
    private const long EpisodeSize = 2000;

    private readonly FakeImportFileSystem _fileSystem = new();
    private readonly FakeMediaProbe _mediaProbe = new();
    private readonly FakeCatalogSeriesQuery _catalog = new();
    private readonly EventSink _sink = new();
    private ServiceProvider _provider = null!;

    private Guid _workId;
    private IReadOnlyList<Guid> _episodeIds = [];

    public async Task InitializeAsync()
    {
        (_workId, _episodeIds) = _catalog.SeedSeries("The Wire", 2002, seasonNumber: 1, episodeCount: 4);
        _provider = await ImportTestHost.CreateAsync(
            "cinomni_test_import_series",
            _fileSystem,
            _mediaProbe,
            services =>
            {
                services.AddSingleton(_sink);
                services.AddScoped<IEventHandler<MediaAvailable>, MediaAvailableSink>();
                services.AddScoped<IEventHandler<ImportCompleted>, ImportCompletedSink>();
                services.AddScoped<IEventHandler<ImportFailed>, ImportFailedSink>();
            },
            _catalog);
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task A_season_pack_lands_every_episode_file()
    {
        SeedPack(1, 2, 3);
        var downloadTaskId = await ImportPackAsync(_episodeIds.Take(3).ToList());

        var job = await GetJobAsync(downloadTaskId);
        Assert.Equal(ImportJobState.Registered, job!.State);
        Assert.Equal(3, job.Matches.Count);
        Assert.All(job.Matches, m => Assert.Equal(ImportFileMatchState.Registered, m.State));
        Assert.Equal(3, _fileSystem.Linked.Count);

        // Each file landed under <Series (Year)>/Season NN/<Series> - S01Exx…
        Assert.All(_fileSystem.Linked, l => Assert.Contains("The Wire (2002)", l.To));
        Assert.All(_fileSystem.Linked, l => Assert.Contains("Season 01", l.To));
        Assert.Contains(_fileSystem.Linked, l => l.To.EndsWith("The Wire - S01E02 - Episode 2.mkv"));
    }

    [Fact]
    public async Task Each_landed_file_gets_its_own_asset_and_media_available()
    {
        SeedPack(1, 2, 3);
        var downloadTaskId = await ImportPackAsync(_episodeIds.Take(3).ToList());

        var job = await GetJobAsync(downloadTaskId);
        Assert.Equal(3, job!.Matches.Select(m => m.AssetId).Distinct().Count());
        Assert.Equal(3, _sink.MediaAvailable.Count);
        Assert.Equal(
            job.Matches.Select(m => m.AssetId).OrderBy(id => id),
            _sink.MediaAvailable.Select(m => m.AssetId).OrderBy(id => id));
    }

    [Fact]
    public async Task Each_media_available_carries_only_its_own_unit()
    {
        SeedPack(1, 2, 3);
        await ImportPackAsync(_episodeIds.Take(3).ToList());

        Assert.Equal(3, _sink.MediaAvailable.Count);
        foreach (var media in _sink.MediaAvailable)
        {
            var unit = Assert.Single(media.UnitIds!);
            Assert.Contains(unit, _episodeIds);
            Assert.Equal(1, media.SeasonNumber);
            Assert.Single(media.EpisodeNumbers!);
        }

        // Three distinct episodes, not the same one three times.
        Assert.Equal(3, _sink.MediaAvailable.SelectMany(m => m.UnitIds!).Distinct().Count());
        Assert.Equal([1, 2, 3], _sink.MediaAvailable.SelectMany(m => m.EpisodeNumbers!).Order());
    }

    [Fact]
    public async Task One_import_completed_is_published_for_the_whole_job()
    {
        SeedPack(1, 2, 3);
        await ImportPackAsync(_episodeIds.Take(3).ToList());

        // Acquisition's goal is per intent: N ImportCompleteds would drive N goal completions.
        var completed = Assert.Single(_sink.Completed);
        Assert.Equal(3, completed.UnitIds!.Count);
    }

    [Fact]
    public async Task A_redelivered_download_completed_imports_nothing_twice()
    {
        SeedPack(1, 2, 3);
        var units = _episodeIds.Take(3).ToList();
        var downloadTaskId = await ImportPackAsync(units);

        // Both guards: the command key is consumed forever, AND the terminal job refuses a re-drive.
        await DeliverAsync(NewDownloadCompleted(downloadTaskId, units));
        await DrainAsync();
        await RedriveAsync(downloadTaskId, units);
        await DrainAsync();

        Assert.Equal(3, _fileSystem.Linked.Count);
        Assert.Equal(3, _sink.MediaAvailable.Count);
        var job = await GetJobAsync(downloadTaskId);
        Assert.Equal(3, job!.Matches.Count);
    }

    [Fact]
    public async Task A_crash_after_three_of_ten_files_resumes_without_relinking()
    {
        SeedPack(1, 2, 3, 4);
        var units = _episodeIds.ToList();
        // The fourth file never lands: verify fails for it, so the batch returns to Pending.
        _fileSystem.FailVerifyFor("The Wire - S01E04 - Episode 4.mkv");

        var downloadTaskId = Uuid7.New();
        await DeliverAsync(NewDownloadCompleted(downloadTaskId, units));
        await DrainAsync();

        var partial = await GetJobAsync(downloadTaskId);
        Assert.Equal(ImportJobState.Pending, partial!.State);
        Assert.Equal(3, partial.Matches.Count(m => m.IsLanded));
        var landedAssetIds = partial.Matches.Where(m => m.IsLanded).Select(m => m.AssetId).OrderBy(id => id).ToList();
        Assert.Equal(4, _fileSystem.Linked.Count);

        // The retry lands only the missing file, and reuses the asset ids minted the first time —
        // a fresh Uuid7 here would break ux_media_versions_full_path in Library. It is driven through
        // the processor rather than a fresh DownloadCompleted, because process-completed-download:{id}
        // is consumed forever: a real retry re-runs the stored command, it does not enqueue a new one.
        _fileSystem.ClearFailures();
        await RedriveAsync(downloadTaskId, units);
        await DrainAsync();

        var resumed = await GetJobAsync(downloadTaskId);
        Assert.Equal(ImportJobState.Registered, resumed!.State);
        Assert.Equal(4, resumed.Matches.Count);
        Assert.Equal(5, _fileSystem.Linked.Count); // 4 first pass (one failed) + 1 retry
        Assert.Equal(
            landedAssetIds,
            resumed.Matches.Where(m => landedAssetIds.Contains(m.AssetId)).Select(m => m.AssetId).OrderBy(id => id));
        Assert.Equal(4, _sink.MediaAvailable.Count);
    }

    [Fact]
    public async Task A_pack_that_loses_one_file_still_registers_the_ones_that_landed()
    {
        SeedPack(1, 2, 3, 4);
        _fileSystem.FailVerifyFor("The Wire - S01E04 - Episode 4.mkv");

        var downloadTaskId = Uuid7.New();
        await DeliverAsync(NewDownloadCompleted(downloadTaskId, _episodeIds.ToList()));
        await DrainAsync();

        // The three files that verified are real assets, not orphan files inside the library tree
        // that Library, Catalog and Monitoring have never heard of.
        Assert.Equal(3, _sink.MediaAvailable.Count);
        var job = await GetJobAsync(downloadTaskId);
        Assert.Equal(3, job!.Matches.Count(m => m.State == ImportFileMatchState.Registered));
        var lost = Assert.Single(job.Matches, m => m.SourcePath.Contains("S01E04"));
        Assert.Equal(ImportFileMatchState.Failed, lost.State);

        // The batch is incomplete, so the job is recoverable rather than terminal — and the goal is
        // told, because process-completed-download:{downloadTaskId} is consumed forever and nothing
        // else can ever re-drive this job.
        Assert.Equal(ImportJobState.Pending, job.State);
        Assert.False(job.IsTerminal);
        Assert.Single(_sink.Failed);
        Assert.Empty(_sink.Completed);
    }

    [Fact]
    public async Task A_second_file_for_the_same_episode_is_refused_instead_of_overwriting_the_first()
    {
        // A pack shipping both the original and its PROPER: the target path is composed only from
        // catalog data, so both resolve to the very same library path.
        _fileSystem.SeedContent(
            ContentPath,
            new ImportFileEntry($"{ContentPath}/The.Wire.S01E01.1080p.mkv", EpisodeSize),
            new ImportFileEntry($"{ContentPath}/The.Wire.S01E01.PROPER.1080p.mkv", EpisodeSize));

        var downloadTaskId = await ImportPackAsync(_episodeIds.Take(1).ToList());

        // One landing, one asset, one MediaAvailable: a second asset on the same full path is
        // schema-impossible (ux_media_versions_full_path) and would fail Library's unit of work,
        // after the hardlink had already replaced the first release on disk.
        var link = Assert.Single(_fileSystem.Linked);
        Assert.EndsWith("The Wire - S01E01 - Episode 1.mkv", link.To);
        Assert.Single(_sink.MediaAvailable);

        var job = await GetJobAsync(downloadTaskId);
        Assert.Equal(2, job!.Matches.Count);
        Assert.Single(job.Matches, m => m.State == ImportFileMatchState.Registered);
        var refused = Assert.Single(job.Matches, m => m.State == ImportFileMatchState.Failed);
        Assert.Contains("already lands at", refused.Reason);
    }

    [Fact]
    public async Task A_date_numbered_file_whose_air_date_is_shared_resolves_to_neither_episode()
    {
        // A two-part finale published under one air date. The file is one part, not both: linking it
        // to both would mark an episode available that no file exists for, and it would never be
        // searched again.
        _catalog.ShareAirDate(_workId, 1, 2);
        var airDate = _catalog.AirDateOf(_workId, 1);
        _fileSystem.SeedContent(
            ContentPath,
            new ImportFileEntry($"{ContentPath}/The.Wire.{airDate:yyyy.MM.dd}.1080p.WEB-DL.mkv", EpisodeSize));

        var downloadTaskId = await ImportPackAsync(_episodeIds.Take(2).ToList());

        var job = await GetJobAsync(downloadTaskId);
        var match = Assert.Single(job!.Matches);
        Assert.Equal(ImportFileMatchState.Unresolved, match.State);
        Assert.Contains("air date is shared", match.Reason);
        Assert.Empty(_fileSystem.Linked);
        Assert.Empty(_sink.MediaAvailable);
    }

    [Fact]
    public async Task A_date_numbered_file_with_a_unique_air_date_still_resolves()
    {
        var airDate = _catalog.AirDateOf(_workId, 2);
        _fileSystem.SeedContent(
            ContentPath,
            new ImportFileEntry($"{ContentPath}/The.Wire.{airDate:yyyy.MM.dd}.1080p.WEB-DL.mkv", EpisodeSize));

        await ImportPackAsync(_episodeIds.Take(2).ToList());

        var media = Assert.Single(_sink.MediaAvailable);
        Assert.Equal(_episodeIds[1], Assert.Single(media.UnitIds!));
    }

    [Fact]
    public async Task A_file_that_resolves_to_no_unit_is_left_unmatched()
    {
        // A stray episode the acquisition never asked for rides along in the pack.
        _fileSystem.SeedContent(
            ContentPath,
            Entry(1),
            Entry(2),
            new ImportFileEntry($"{ContentPath}/The.Wire.S09E99.1080p.mkv", EpisodeSize));

        var downloadTaskId = await ImportPackAsync(_episodeIds.Take(2).ToList());

        var job = await GetJobAsync(downloadTaskId);
        Assert.Equal(ImportJobState.Registered, job!.State);
        var stray = Assert.Single(job.Matches, m => m.SourcePath.Contains("S09E99"));
        Assert.Equal(ImportFileMatchState.Unresolved, stray.State);
        Assert.False(string.IsNullOrEmpty(stray.Reason));
        Assert.Equal(2, _fileSystem.Linked.Count);
        Assert.Equal(2, _sink.MediaAvailable.Count);
    }

    [Fact]
    public async Task An_episode_outside_the_requested_units_is_not_linked()
    {
        SeedPack(1, 2, 3);
        // Only episodes 1 and 2 were requested; episode 3 is already in the library.
        var downloadTaskId = await ImportPackAsync(_episodeIds.Take(2).ToList());

        var job = await GetJobAsync(downloadTaskId);
        var skipped = Assert.Single(job!.Matches, m => m.SourcePath.Contains("S01E03"));
        Assert.Equal(ImportFileMatchState.Unresolved, skipped.State);
        Assert.Contains("outside the requested units", skipped.Reason);
        Assert.Equal(2, _fileSystem.Linked.Count);
    }

    [Fact]
    public async Task A_multi_episode_file_is_one_asset_with_two_units()
    {
        _fileSystem.SeedContent(
            ContentPath,
            new ImportFileEntry($"{ContentPath}/The.Wire.S01E01-E02.1080p.mkv", EpisodeSize));

        await ImportPackAsync(_episodeIds.Take(2).ToList());

        // ux_media_versions_full_path is unique on the version path, so two assets for one physical
        // file is schema-impossible: it must be one asset with two unit links.
        var media = Assert.Single(_sink.MediaAvailable);
        Assert.Equal(2, media.UnitIds!.Count);
        Assert.Equal([1, 2], media.EpisodeNumbers!);
        Assert.EndsWith("The Wire - S01E01-E02 - Episode 1.mkv", media.FullPath);
    }

    [Fact]
    public async Task Each_file_is_probed_and_registered_with_its_own_streams()
    {
        SeedPack(1, 2);
        _mediaProbe.ScriptForFile(
            "The Wire - S01E02 - Episode 2.mkv",
            new MediaInfo("matroska", 1400, 3_000_000,
                [new MediaStreamInfo(0, MediaStreamKind.Video, "hevc", null, 3840, 2160, null, true, false)]));

        await ImportPackAsync(_episodeIds.Take(2).ToList());

        var second = Assert.Single(_sink.MediaAvailable, m => m.EpisodeNumbers![0] == 2);
        Assert.Equal("hevc", Assert.Single(second.MediaInfo.Streams).Codec);
        var first = Assert.Single(_sink.MediaAvailable, m => m.EpisodeNumbers![0] == 1);
        Assert.Equal(2, first.MediaInfo.Streams.Count);
    }

    // -- helpers ---------------------------------------------------------------------------------

    private static ImportFileEntry Entry(int episode) =>
        new($"{ContentPath}/The.Wire.S01E{episode:D2}.1080p.BluRay.x264.mkv", EpisodeSize);

    private void SeedPack(params int[] episodes) =>
        _fileSystem.SeedContent(ContentPath, [.. episodes.Select(Entry)]);

    private DownloadCompleted NewDownloadCompleted(Guid downloadTaskId, IReadOnlyList<Guid> units) =>
        new(downloadTaskId, Guid.Empty, Guid.Empty, _workId, Uuid7.New(), "infohash", ContentPath, UnitIds: units);

    private async Task<Guid> ImportPackAsync(IReadOnlyList<Guid> units)
    {
        var downloadTaskId = Uuid7.New();
        await DeliverAsync(NewDownloadCompleted(downloadTaskId, units));
        await DrainAsync();
        return downloadTaskId;
    }

    /// <summary>Re-runs the import for a download exactly as a retried stored command would.</summary>
    private async Task RedriveAsync(Guid downloadTaskId, IReadOnlyList<Guid> units)
    {
        await using var scope = _provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IImportProcessor>().ProcessCompletedDownloadAsync(
            downloadTaskId, Guid.Empty, Guid.Empty, _workId, Uuid7.New(), ContentPath, units);
    }

    private async Task DeliverAsync(DownloadCompleted domainEvent)
    {
        await using var scope = _provider.CreateAsyncScope();
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
            .Include(j => j.Matches)
            .Include(j => j.Operations)
            .Include(j => j.History)
            .FirstOrDefaultAsync(j => j.DownloadTaskId == downloadTaskId);
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
        public ConcurrentBag<MediaAvailable> MediaAvailable { get; } = [];

        public ConcurrentBag<ImportCompleted> Completed { get; } = [];

        public ConcurrentBag<ImportFailed> Failed { get; } = [];
    }

    private sealed class MediaAvailableSink(EventSink sink) : IEventHandler<MediaAvailable>
    {
        public Task HandleAsync(MediaAvailable domainEvent, CancellationToken cancellationToken = default)
        {
            sink.MediaAvailable.Add(domainEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class ImportCompletedSink(EventSink sink) : IEventHandler<ImportCompleted>
    {
        public Task HandleAsync(ImportCompleted domainEvent, CancellationToken cancellationToken = default)
        {
            sink.Completed.Add(domainEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class ImportFailedSink(EventSink sink) : IEventHandler<ImportFailed>
    {
        public Task HandleAsync(ImportFailed domainEvent, CancellationToken cancellationToken = default)
        {
            sink.Failed.Add(domainEvent);
            return Task.CompletedTask;
        }
    }
}
