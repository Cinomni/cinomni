using System.Text;
using Cinomni.Catalog.Contracts;
using Cinomni.Kernel.Identifiers;
using Cinomni.Kernel.Messaging;
using Cinomni.Library.Contracts;
using Cinomni.Operations.Messaging;
using Cinomni.Operations.Persistence;
using Cinomni.Subtitles.Contracts;
using Cinomni.Subtitles.Messaging;
using Cinomni.Subtitles.Persistence;
using Cinomni.Subtitles.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Subtitles.Tests;

/// <summary>
/// Integration tests for subtitle synchronization against a real PostgreSQL instance: a subtitle lands
/// playable and synchronizing in one unit of work, the sync processor (driven by hand, as the hosted
/// worker would) decides and records why, and every way it can go wrong still ends with the subtitle
/// available. Speech comes from a fake source scripted with generated dialogue, so the whole pass runs
/// without FFmpeg.
/// </summary>
public sealed class SubtitleSyncFlowTests : IAsyncLifetime
{
    private const string MoviePath = "/data/library/Movie.2024/Movie.2024.mkv";
    // Composed as the store composes them, so the separators are the platform's.
    private static readonly string OriginalPath = Path.Combine(Path.GetDirectoryName(MoviePath)!, "Movie.2024.en.srt");
    private static readonly string SyncedPath = Path.Combine(Path.GetDirectoryName(MoviePath)!, "Movie.2024.en.synced.srt");
    private const long SpokenLateMs = 1_200;

    private readonly FakeSubtitleProvider _provider = new();
    private readonly FakeSubtitleFileStore _fileStore = new();
    private readonly FakeSpeechActivitySource _speech = new();
    private readonly SyntheticDialogue _dialogue = new(seed: 31, TimeSpan.FromHours(1));
    private ServiceProvider _host = null!;

    public async Task InitializeAsync()
    {
        _provider.Content = Encoding.UTF8.GetBytes(_dialogue.ToSrt());
        _speech.Speech = (start, length) => _dialogue.Speech(start, length, SpokenLateMs);
        _host = await SubtitlesTestHost.CreateAsync(
            "cinomni_test_subtitles_sync",
            _provider,
            _fileStore,
            configure: services =>
            {
                services.AddSingleton(new SubtitleSyncOptions());
                services.AddSingleton<ISpeechActivitySource>(_speech);
            });
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task A_landed_subtitle_is_playable_synchronizing_and_queued_in_one_unit_of_work()
    {
        // Act
        var assetId = await RegisterAssetAsync();
        await DrainAsync();

        // Assert — the search waits on the sync, the subtitle plays the download meanwhile, the work is
        // queued, and Library already heard about the subtitle.
        var search = Assert.Single(await QueryAsync(q => q.ListForAssetAsync(assetId)));
        Assert.Equal(SubtitleSearchState.Syncing, search.State);
        var subtitle = Assert.Single(await QueryAsync(q => q.ListObtainedAsync(assetId)));
        Assert.Equal(OriginalPath, subtitle.Path);
        Assert.Null(subtitle.Sync);
        Assert.Equal(subtitle.Id.Value, search.SubtitleId);
        Assert.Equal(1, await CountWorkAsync());
        Assert.Equal(1, await CountOutboxAsync(SubtitleEventNames.SubtitleAvailable));

        var library = await LibraryAsync(assetId);
        Assert.Contains(library.Versions[0].Streams, s => s.Type == MediaStreamType.Subtitle && s.IsExternal);
    }

    [Fact]
    public async Task Processing_applies_the_offset_writes_a_copy_and_completes_the_search()
    {
        // Arrange
        var assetId = await RegisterAssetAsync();
        await DrainAsync();

        // Act
        Assert.True(await ProcessNextAsync());

        // Assert
        var search = Assert.Single(await QueryAsync(q => q.ListForAssetAsync(assetId)));
        Assert.Equal(SubtitleSearchState.Available, search.State);
        var subtitle = Assert.Single(await QueryAsync(q => q.ListObtainedAsync(assetId)));
        Assert.Equal(SyncedPath, subtitle.Path);
        var sync = Assert.IsType<SubtitleSyncSummary>(subtitle.Sync);
        Assert.Equal(SubtitleSyncOutcome.Applied, sync.Outcome);
        Assert.InRange(sync.OffsetMs, SpokenLateMs - 20, SpokenLateMs + 20);
        Assert.Equal(1.0, sync.Ratio);
        Assert.True(sync.HasOriginal);
        Assert.Equal(0, await CountWorkAsync());

        // The copy moved every cue; the download was not touched.
        var first = _dialogue.Cues[0];
        var copy = Encoding.UTF8.GetString(_fileStore.Files[SyncedPath]);
        Assert.Contains(SyntheticDialogue.Timestamp(first.StartMs + sync.OffsetMs) + " --> ", copy, StringComparison.Ordinal);
        Assert.Equal(_provider.Content, _fileStore.Files[OriginalPath]);

        // Three stretches of the English track were read.
        Assert.Equal(3, _speech.Requests.Count);
        Assert.All(_speech.Requests, request => Assert.Equal((MoviePath, 1), (request.MediaPath, request.AudioStream)));
    }

    [Fact]
    public async Task Processing_again_or_a_stale_row_changes_nothing()
    {
        // Arrange
        var assetId = await RegisterAssetAsync();
        await DrainAsync();
        Assert.True(await ProcessNextAsync());
        var subtitle = Assert.Single(await QueryAsync(q => q.ListObtainedAsync(assetId)));

        // Act — nothing pending; then a row left behind for a subtitle already decided.
        var again = await ProcessNextAsync();
        await WithDbAsync(async db =>
        {
            var search = await db.Searches.SingleAsync(s => s.AssetId == assetId);
            db.SyncWork.Add(SubtitleSyncWork.Create(subtitle.Id.Value, search.Id, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        });
        var stale = await ProcessNextAsync();

        // Assert — the stale row is consumed without a second decision or a second pass.
        Assert.False(again);
        Assert.True(stale);
        Assert.Equal(0, await CountWorkAsync());
        Assert.Equal(1, await CountDecisionsAsync());
        Assert.Equal(3, _speech.Requests.Count);
        Assert.Equal(SubtitleSearchState.Available, Assert.Single(await QueryAsync(q => q.ListForAssetAsync(assetId))).State);
    }

    [Fact]
    public async Task An_analysis_that_throws_keeps_the_original_and_completes()
    {
        // Arrange
        _speech.Exception = new InvalidOperationException("decoder exploded");
        var assetId = await RegisterAssetAsync();
        await DrainAsync();

        // Act
        Assert.True(await ProcessNextAsync());

        // Assert
        Assert.Equal(SubtitleSearchState.Available, Assert.Single(await QueryAsync(q => q.ListForAssetAsync(assetId))).State);
        var subtitle = Assert.Single(await QueryAsync(q => q.ListObtainedAsync(assetId)));
        Assert.Equal(OriginalPath, subtitle.Path);
        Assert.Equal(SubtitleSyncOutcome.Failed, subtitle.Sync!.Outcome);
        Assert.Equal("Kept the original because the analysis failed unexpectedly.", subtitle.Sync.Reason);
        Assert.False(subtitle.Sync.HasOriginal);
        Assert.False(_fileStore.Files.ContainsKey(SyncedPath));
    }

    [Fact]
    public async Task Ffmpeg_failing_is_recorded_as_a_failure_that_keeps_the_original()
    {
        _speech.Failure = "FFmpeg could not be started";
        var assetId = await RegisterAssetAsync();
        await DrainAsync();

        await ProcessNextAsync();

        var subtitle = Assert.Single(await QueryAsync(q => q.ListObtainedAsync(assetId)));
        Assert.Equal(OriginalPath, subtitle.Path);
        Assert.Equal("Kept the original because FFmpeg could not be started.", subtitle.Sync!.Reason);
    }

    [Fact]
    public async Task A_video_without_audio_is_recorded_as_unsupported()
    {
        var assetId = await RegisterAssetAsync(withAudio: false);
        await DrainAsync();

        await ProcessNextAsync();

        var subtitle = Assert.Single(await QueryAsync(q => q.ListObtainedAsync(assetId)));
        Assert.Equal(SubtitleSyncOutcome.Unsupported, subtitle.Sync!.Outcome);
        Assert.Equal(OriginalPath, subtitle.Path);
        Assert.Empty(_speech.Requests);
    }

    [Fact]
    public async Task The_sweep_closes_a_search_left_synchronizing_and_leaves_queued_work_alone()
    {
        // Arrange — one subtitle queued normally, then its pending row lost (as if written by hand away).
        var assetId = await RegisterAssetAsync();
        await DrainAsync();
        var later = DateTimeOffset.UtcNow.AddHours(2);

        // Act / Assert — still queued: not stuck, just waiting its turn.
        Assert.Equal(0, await SweepAsync(later));

        await WithDbAsync(db => db.SyncWork.ExecuteDeleteAsync());
        Assert.Equal(0, await SweepAsync(DateTimeOffset.UtcNow));
        Assert.Equal(1, await SweepAsync(later));
        Assert.Equal(0, await SweepAsync(later));

        Assert.Equal(SubtitleSearchState.Available, Assert.Single(await QueryAsync(q => q.ListForAssetAsync(assetId))).State);
        var subtitle = Assert.Single(await QueryAsync(q => q.ListObtainedAsync(assetId)));
        Assert.Equal(SubtitleSyncOutcome.Failed, subtitle.Sync!.Outcome);
        Assert.StartsWith("Kept the original because synchronization did not finish", subtitle.Sync.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_sweep_closes_a_pass_claimed_long_ago_and_the_late_pass_then_writes_nothing()
    {
        // Arrange — the row is claimed (a pass started) and never finished.
        var assetId = await RegisterAssetAsync();
        await DrainAsync();
        await WithDbAsync(db => db.SyncWork.ExecuteUpdateAsync(
            set => set.SetProperty(w => w.ClaimedAt, DateTimeOffset.UtcNow.AddHours(-3))));

        // Act
        var closed = await SweepAsync(DateTimeOffset.UtcNow.AddHours(2));
        var processedLate = await ProcessNextAsync();

        // Assert
        Assert.Equal(1, closed);
        Assert.False(processedLate);
        Assert.Equal(1, await CountDecisionsAsync());
        Assert.Equal(0, await CountWorkAsync());
        Assert.Equal(SubtitleSearchState.Available, Assert.Single(await QueryAsync(q => q.ListForAssetAsync(assetId))).State);
    }

    [Fact]
    public async Task Revert_puts_the_download_back_and_resync_runs_a_fresh_pass_over_it()
    {
        // Arrange — synchronized once.
        var assetId = await RegisterAssetAsync();
        await DrainAsync();
        await ProcessNextAsync();
        var subtitleId = Assert.Single(await QueryAsync(q => q.ListObtainedAsync(assetId))).Id;

        // Act — revert.
        var reverted = await CommandAsync(c => c.RevertAsync(subtitleId));
        var revertedAgain = await CommandAsync(c => c.RevertAsync(subtitleId));

        // Assert
        Assert.True(reverted.IsSuccess);
        Assert.Equal(SubtitleSyncCommands.NotSynced, revertedAgain.Error);
        var subtitle = Assert.Single(await QueryAsync(q => q.ListObtainedAsync(assetId)));
        Assert.Equal(OriginalPath, subtitle.Path);
        Assert.Equal(SubtitleSyncOutcome.Reverted, subtitle.Sync!.Outcome);
        Assert.False(subtitle.Sync.HasOriginal);
        Assert.Contains(SyncedPath, _fileStore.Deleted);

        // Act — resync, twice, then process.
        var resync = await CommandAsync(c => c.ResyncAsync(subtitleId));
        var resyncAgain = await CommandAsync(c => c.ResyncAsync(subtitleId));
        Assert.Equal(SubtitleSyncCommands.InProgress, (await CommandAsync(c => c.RevertAsync(subtitleId))).Error);
        Assert.Equal(SubtitleSearchState.Syncing, Assert.Single(await QueryAsync(q => q.ListForAssetAsync(assetId))).State);
        await ProcessNextAsync();

        // Assert — applied again, from the download, and the history has all three decisions.
        Assert.True(resync.IsSuccess);
        Assert.Equal(SubtitleSyncCommands.InProgress, resyncAgain.Error);
        subtitle = Assert.Single(await QueryAsync(q => q.ListObtainedAsync(assetId)));
        Assert.Equal(SyncedPath, subtitle.Path);
        Assert.Equal(SubtitleSyncOutcome.Applied, subtitle.Sync!.Outcome);
        Assert.InRange(subtitle.Sync.OffsetMs, SpokenLateMs - 20, SpokenLateMs + 20);
        Assert.Equal(3, await CountDecisionsAsync());
        Assert.Equal(SubtitleSearchState.Available, Assert.Single(await QueryAsync(q => q.ListForAssetAsync(assetId))).State);
    }

    [Fact]
    public async Task A_resync_that_is_no_longer_confident_goes_back_to_the_download()
    {
        // Arrange — applied once; then the audio stops matching anything.
        var assetId = await RegisterAssetAsync();
        await DrainAsync();
        await ProcessNextAsync();
        var subtitleId = Assert.Single(await QueryAsync(q => q.ListObtainedAsync(assetId))).Id;
        _speech.Speech = _dialogue.RandomSpeech;

        // Act
        await CommandAsync(c => c.ResyncAsync(subtitleId));
        await ProcessNextAsync();

        // Assert
        var subtitle = Assert.Single(await QueryAsync(q => q.ListObtainedAsync(assetId)));
        Assert.Equal(SubtitleSyncOutcome.LowConfidence, subtitle.Sync!.Outcome);
        Assert.Equal(OriginalPath, subtitle.Path);
        Assert.Contains(SyncedPath, _fileStore.Deleted);
    }

    [Fact]
    public async Task Unknown_subtitles_are_not_found()
    {
        var unknown = new SubtitleAssetId(Guid.NewGuid());

        Assert.Equal(SubtitleSyncCommands.NotFound, (await CommandAsync(c => c.ResyncAsync(unknown))).Error);
        Assert.Equal(SubtitleSyncCommands.NotFound, (await CommandAsync(c => c.RevertAsync(unknown))).Error);
    }

    [Fact]
    public async Task Relocation_moves_both_the_synchronized_copy_and_the_original()
    {
        // Arrange
        var assetId = await RegisterAssetAsync();
        await DrainAsync();
        await ProcessNextAsync();

        // Act
        await using (var scope = _host.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ICommandHandler<RelocateSubtitlesCommand>>().HandleAsync(
                new RelocateSubtitlesCommand(assetId, MoviePath, "/data/library/Movie (2024)/Movie (2024).mkv"));
        }

        // Assert — and a revert afterwards lands on the moved original.
        Assert.Equal(
            "/data/library/Movie (2024)/Movie (2024).en.synced.srt",
            Assert.Single(await QueryAsync(q => q.ListObtainedAsync(assetId))).Path.Replace('\\', '/'));
        var subtitleId = Assert.Single(await QueryAsync(q => q.ListObtainedAsync(assetId))).Id;
        await CommandAsync(c => c.RevertAsync(subtitleId));
        Assert.Equal(
            "/data/library/Movie (2024)/Movie (2024).en.srt",
            Assert.Single(await QueryAsync(q => q.ListObtainedAsync(assetId))).Path.Replace('\\', '/'));
    }

    // -- helpers ---------------------------------------------------------------------------------

    private async Task<Guid> RegisterAssetAsync(bool withAudio = true)
    {
        var assetId = Uuid7.New();
        var streams = new List<MediaStreamInput>
        {
            new(0, MediaStreamType.Video, "h264", null, null, 1920, 1080, null, null, true, false),
        };
        if (withAudio)
        {
            streams.Add(new MediaStreamInput(1, MediaStreamType.Audio, "aac", "eng", 6, null, null, null, null, true, false));
            streams.Add(new MediaStreamInput(2, MediaStreamType.Audio, "aac", "spa", 6, null, null, null, null, false, false));
        }

        var request = new RegisterMediaAssetRequest(
            assetId, await AddWorkAsync(), [Uuid7.New()], MoviePath, 2_000_000_000, "matroska", streams,
            DurationSeconds: 3_600);

        await using var scope = _host.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ILibraryCommands>().RegisterMediaAssetAsync(request);
        return assetId;
    }

    private async Task<bool> ProcessNextAsync()
    {
        await using var scope = _host.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<SubtitleSyncProcessor>().ProcessNextAsync();
    }

    private async Task<int> SweepAsync(DateTimeOffset now)
    {
        await using var scope = _host.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<SubtitleSyncProcessor>().SweepAsync(now);
    }

    private async Task<Cinomni.Kernel.Results.Result> CommandAsync(
        Func<SubtitleSyncCommands, Task<Cinomni.Kernel.Results.Result>> command)
    {
        await using var scope = _host.CreateAsyncScope();
        return await command(scope.ServiceProvider.GetRequiredService<SubtitleSyncCommands>());
    }

    private async Task<T> QueryAsync<T>(Func<ISubtitleQuery, Task<T>> query)
    {
        await using var scope = _host.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<ISubtitleQuery>());
    }

    private async Task<MediaAssetDetail> LibraryAsync(Guid assetId)
    {
        await using var scope = _host.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<ILibraryQuery>().GetAsync(new MediaAssetId(assetId)))!;
    }

    private async Task WithDbAsync(Func<SubtitlesDbContext, Task> work)
    {
        await using var scope = _host.CreateAsyncScope();
        await work(scope.ServiceProvider.GetRequiredService<SubtitlesDbContext>());
    }

    private async Task<int> CountWorkAsync()
    {
        await using var scope = _host.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<SubtitlesDbContext>().SyncWork.CountAsync();
    }

    private async Task<int> CountDecisionsAsync()
    {
        await using var scope = _host.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<SubtitlesDbContext>().SyncDecisions.CountAsync();
    }

    private async Task<int> CountOutboxAsync(string type)
    {
        await using var scope = _host.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OperationsDbContext>().Outbox.CountAsync(m => m.EventType == type);
    }

    private async Task DrainAsync()
    {
        while (true)
        {
            int commands;
            int events;
            await using (var scope = _host.CreateAsyncScope())
            {
                var processor = scope.ServiceProvider.GetRequiredService<CommandProcessor>();
                commands = 0;
                int processed;
                while ((processed = await processor.ProcessBatchAsync()) > 0)
                {
                    commands += processed;
                }
            }

            await using (var scope = _host.CreateAsyncScope())
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

    private async Task<Guid> AddWorkAsync()
    {
        await using var scope = _host.CreateAsyncScope();
        var added = await scope.ServiceProvider.GetRequiredService<ICatalogCommands>().AddMovieAsync("The Matrix", 1999, []);
        return added.Value.Value;
    }
}
