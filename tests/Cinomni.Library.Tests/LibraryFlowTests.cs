using System.Collections.Concurrent;
using Cinomni.Catalog.Contracts;
using Cinomni.Import.Contracts;
using Cinomni.Kernel.Identifiers;
using Cinomni.Kernel.Messaging;
using Cinomni.Library.Contracts;
using Cinomni.Operations.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Library.Tests;

/// <summary>
/// Integration tests for the library spine against a real PostgreSQL instance: Import's
/// MediaAvailable fans out so Library registers the asset (with its version and relational streams)
/// and Catalog marks the work available. Events are driven by alternately draining the outbox and the
/// command queue, exactly as the hosted services would.
/// </summary>
public sealed class LibraryFlowTests : IAsyncLifetime
{
    private readonly EventSink _sink = new();
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync() =>
        _provider = await LibraryTestHost.CreateAsync("cinomni_test_library", services =>
        {
            services.AddSingleton(_sink);
            services.AddScoped<IEventHandler<MediaAssetRegistered>, RegisteredSink>();
            services.AddScoped<IEventHandler<WorkAvailable>, WorkAvailableSink>();
        });

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task MediaAvailable_registers_the_asset_and_marks_the_work_available()
    {
        var workId = await AddMovieAsync();
        var targetId = Uuid7.New();
        var assetId = Uuid7.New();

        await DeliverMediaAvailableAsync(NewMediaAvailable(assetId, workId, targetId));
        await DrainAsync();

        // Library owns the rows: asset + primary version + relational streams + target link.
        var detail = await QueryLibraryAsync(q => q.GetAsync(new MediaAssetId(assetId)));
        Assert.NotNull(detail);
        Assert.Equal(MediaAssetState.Active, detail!.Asset.State);
        Assert.Equal(workId, detail.Asset.WorkId);
        var version = Assert.Single(detail.Versions);
        Assert.Equal(2, version.Streams.Count);
        Assert.Equal("1080p", await ResolutionOfAsync(assetId));
        Assert.Contains(version.Streams, s => s.Type == MediaStreamType.Video && s.Codec == "h264");
        Assert.Equal(targetId, Assert.Single(detail.TargetIds));

        // Catalog reacted independently and marked the work available.
        var work = await QueryCatalogAsync(q => q.GetByIdAsync(new WorkId(workId)));
        Assert.True(work!.HasAsset);

        // Both facts were announced.
        Assert.Contains(assetId, _sink.Registered);
        Assert.Contains(workId, _sink.WorkAvailable);
    }

    [Fact]
    public async Task Registering_the_same_asset_twice_creates_one_asset()
    {
        var workId = await AddMovieAsync();
        var assetId = Uuid7.New();
        var media = NewMediaAvailable(assetId, workId, Uuid7.New());

        await DeliverMediaAvailableAsync(media);
        await DrainAsync();
        await DeliverMediaAvailableAsync(media); // redelivery (at-least-once)
        await DrainAsync();

        var assets = await QueryLibraryAsync(q => q.GetByWorkAsync(workId));
        Assert.Single(assets);
    }

    [Fact]
    public async Task An_asset_with_no_targets_still_registers()
    {
        var workId = await AddMovieAsync();
        var assetId = Uuid7.New();

        await DeliverMediaAvailableAsync(NewMediaAvailable(assetId, workId, targetId: null));
        await DrainAsync();

        var detail = await QueryLibraryAsync(q => q.GetAsync(new MediaAssetId(assetId)));
        Assert.NotNull(detail);
        Assert.Empty(detail!.TargetIds);
    }

    [Fact]
    public async Task A_movie_import_carries_the_work_id_as_its_single_unit()
    {
        var workId = await AddMovieAsync();
        var targetId = Uuid7.New();
        var assetId = Uuid7.New();
        var media = NewMediaAvailable(assetId, workId, targetId);

        // Import publishes the movie unit alongside the acquiring target; the two id spaces are
        // distinct but coincide for a movie.
        Assert.Equal(workId, Assert.Single(media.UnitIds!));
        Assert.Equal(targetId, Assert.Single(media.TargetIds));
        Assert.NotEqual(media.UnitIds![0], media.TargetIds[0]);

        await DeliverMediaAvailableAsync(media);
        await DrainAsync();

        // The widened payload changes nothing Library persists: still one asset on the target link.
        var detail = await QueryLibraryAsync(q => q.GetAsync(new MediaAssetId(assetId)));
        Assert.Equal(workId, detail!.Asset.WorkId);
        Assert.Equal(targetId, Assert.Single(detail.TargetIds));
    }

    [Theory]
    [InlineData("DoVi", VideoRangeType.DoVi)]
    [InlineData("Hdr10", VideoRangeType.Hdr10)]
    [InlineData("Sdr", VideoRangeType.Sdr)]
    [InlineData("Hdr11", null)]
    [InlineData("dovi", null)]
    [InlineData("99", null)]
    [InlineData("2,4", null)]
    [InlineData(null, null)]
    public async Task The_dynamic_range_Import_read_is_what_the_video_stream_records(string? published, VideoRangeType? stored)
    {
        var workId = await AddMovieAsync();
        var assetId = Uuid7.New();

        await DeliverMediaAvailableAsync(NewMediaAvailable(assetId, workId, Uuid7.New(), published));
        await DrainAsync();

        var detail = await QueryLibraryAsync(q => q.GetAsync(new MediaAssetId(assetId)));
        var streams = Assert.Single(detail!.Versions).Streams;
        Assert.Equal(stored, streams.Single(s => s.Type == MediaStreamType.Video).VideoRangeType);
        Assert.Null(streams.Single(s => s.Type == MediaStreamType.Audio).VideoRangeType);
    }

    // -- helpers ---------------------------------------------------------------------------------

    private static MediaAvailable NewMediaAvailable(Guid assetId, Guid workId, Guid? targetId, string? videoRange = null)
    {
        IReadOnlyList<Guid> targetIds = targetId is { } id ? [id] : [];
        var mediaInfo = new MediaInfo(
            "matroska,webm",
            DurationSeconds: 6000,
            Bitrate: 8_000_000,
            Streams:
            [
                new MediaStreamInfo(0, MediaStreamKind.Video, "h264", null, 1920, 1080, null, IsDefault: true, IsForced: false, videoRange),
                new MediaStreamInfo(1, MediaStreamKind.Audio, "aac", "eng", null, null, 6, IsDefault: true, IsForced: false),
            ]);
        // Mirrors what ImportService publishes for a movie: the unit is the work itself.
        return new MediaAvailable(
            assetId, workId, targetIds, Uuid7.New(), Uuid7.New(), "/data/library/Movie/Movie.mkv", 2_000_000_000, mediaInfo,
            UnitIds: [workId]);
    }

    private async Task<Guid> AddMovieAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var commands = scope.ServiceProvider.GetRequiredService<ICatalogCommands>();
        var result = await commands.AddMovieAsync("Test Movie", 2024, []);
        return result.Value.Value;
    }

    private async Task DeliverMediaAvailableAsync(MediaAvailable domainEvent)
    {
        await using var scope = _provider.CreateAsyncScope();
        // The outbox relay fans an event out to every registered consumer; do the same so both
        // Library and Catalog react.
        foreach (var handler in scope.ServiceProvider.GetServices<IEventHandler<MediaAvailable>>())
        {
            await handler.HandleAsync(domainEvent);
        }
    }

    private async Task<string?> ResolutionOfAsync(Guid assetId)
    {
        var detail = await QueryLibraryAsync(q => q.GetAsync(new MediaAssetId(assetId)));
        var video = detail!.Versions[0].Streams.First(s => s.Type == MediaStreamType.Video);
        return video.Height switch { >= 1000 => "1080p", _ => "other" };
    }

    private async Task<T> QueryLibraryAsync<T>(Func<ILibraryQuery, Task<T>> query)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<ILibraryQuery>());
    }

    private async Task<T> QueryCatalogAsync<T>(Func<ICatalogQuery, Task<T>> query)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<ICatalogQuery>());
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
        public ConcurrentBag<Guid> Registered { get; } = [];

        public ConcurrentBag<Guid> WorkAvailable { get; } = [];
    }

    private sealed class RegisteredSink(EventSink sink) : IEventHandler<MediaAssetRegistered>
    {
        public Task HandleAsync(MediaAssetRegistered domainEvent, CancellationToken cancellationToken = default)
        {
            sink.Registered.Add(domainEvent.AssetId);
            return Task.CompletedTask;
        }
    }

    private sealed class WorkAvailableSink(EventSink sink) : IEventHandler<WorkAvailable>
    {
        public Task HandleAsync(WorkAvailable domainEvent, CancellationToken cancellationToken = default)
        {
            sink.WorkAvailable.Add(domainEvent.WorkId);
            return Task.CompletedTask;
        }
    }
}
