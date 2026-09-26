using Cinomni.Catalog.Contracts;
using Cinomni.Kernel.Identifiers;
using Cinomni.Library.Contracts;
using Cinomni.Operations.Messaging;
using Cinomni.Subtitles.Application;
using Cinomni.Subtitles.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Subtitles.Tests;

/// <summary>
/// Integration tests for the subtitle spine against a real PostgreSQL instance: Library's
/// MediaAssetRegistered triggers a search, the (faked) provider yields a candidate that is downloaded
/// and written, and SubtitleAvailable flows back so Library enriches the asset with an external
/// subtitle track. Events are driven by alternately draining the outbox and the command queue.
/// </summary>
public sealed class SubtitleFlowTests : IAsyncLifetime
{
    private const string MoviePath = "/data/library/Movie.2024/Movie.2024.mkv";

    private readonly FakeSubtitleProvider _provider = new();
    private readonly FakeSubtitleFileStore _fileStore = new();
    private ServiceProvider _host = null!;

    public async Task InitializeAsync() =>
        _host = await SubtitlesTestHost.CreateAsync("cinomni_test_subtitles", _provider, _fileStore);

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task A_registered_asset_gets_a_subtitle_and_library_is_enriched()
    {
        var assetId = await RegisterAssetAsync(withEnglishSubtitle: false);

        await DrainAsync();

        // The search landed a subtitle and wrote it next to the video.
        var searches = await QuerySubtitlesAsync(q => q.ListForAssetAsync(assetId));
        var search = Assert.Single(searches);
        Assert.Equal(SubtitleSearchState.Available, search.State);
        Assert.Contains(_fileStore.Written, w => w.Language == "en" && w.VideoPath == MoviePath);

        // Library was enriched with an external subtitle track.
        var detail = await QueryLibraryAsync(q => q.GetAsync(new MediaAssetId(assetId)));
        var subtitle = Assert.Single(detail!.Versions[0].Streams, s => s.Type == MediaStreamType.Subtitle && s.IsExternal);
        Assert.Equal("en", subtitle.Language);
    }

    [Fact]
    public async Task No_candidate_over_the_threshold_leaves_the_search_not_found()
    {
        _provider.Candidates = [new Providers.ProviderCandidate("Movie", 2, false, SubtitleFormat.Srt, "file-lowscore")];
        var assetId = await RegisterAssetAsync(withEnglishSubtitle: false);

        await DrainAsync();

        var search = Assert.Single(await QuerySubtitlesAsync(q => q.ListForAssetAsync(assetId)));
        Assert.Equal(SubtitleSearchState.NotFound, search.State);
        Assert.Empty(_fileStore.Written);

        var detail = await QueryLibraryAsync(q => q.GetAsync(new MediaAssetId(assetId)));
        Assert.DoesNotContain(detail!.Versions[0].Streams, s => s.IsExternal);
    }

    [Fact]
    public async Task A_language_already_present_is_not_searched()
    {
        var assetId = await RegisterAssetAsync(withEnglishSubtitle: true);

        await DrainAsync();

        // Not searched and nothing downloaded — but written down as covered, so the catch-up does not
        // read the language as missing on every pass.
        var recorded = Assert.Single(await QuerySubtitlesAsync(q => q.ListForAssetAsync(assetId)));
        Assert.Equal(SubtitleSearchState.Available, recorded.State);
        Assert.Equal(0, recorded.Attempts);
        Assert.Empty(_provider.Searches);
        Assert.Empty(_fileStore.Written);
    }

    [Fact]
    public async Task The_catch_up_moves_past_assets_that_need_nothing_to_the_ones_that_do()
    {
        // Arrange — more covered assets than one pass may queue, then one that is missing its subtitle.
        var covered = new List<Guid>();
        for (var i = 0; i < SubtitleCatchUp.MaxPerRun + 5; i++)
        {
            covered.Add(await RegisterAssetAsync(withEnglishSubtitle: true, $"/data/library/Covered.{i}/Covered.{i}.mkv"));
        }

        var missing = await RegisterAssetAsync(withEnglishSubtitle: false);
        await DrainAsync();
        _provider.Searches.Clear();

        // Act — two passes, as the schedule would run them.
        int first;
        int second;
        await using (var scope = _host.CreateAsyncScope())
        {
            first = await scope.ServiceProvider.GetRequiredService<SubtitleCatchUp>().EnqueueAsync();
        }

        await using (var scope = _host.CreateAsyncScope())
        {
            second = await scope.ServiceProvider.GetRequiredService<SubtitleCatchUp>().EnqueueAsync();
        }

        // Assert — the covered ones are covered, so nothing is re-queued for them, and nothing stalls.
        Assert.Equal(0, first);
        Assert.Equal(0, second);
        Assert.Equal(SubtitleSearchState.Available, Assert.Single(await QuerySubtitlesAsync(q => q.ListForAssetAsync(missing))).State);
        Assert.Equal(SubtitleSearchState.Available, Assert.Single(await QuerySubtitlesAsync(q => q.ListForAssetAsync(covered[^1]))).State);
        Assert.Empty(_provider.Searches);
    }

    [Fact]
    public async Task Re_triggering_searches_a_language_once()
    {
        var assetId = await RegisterAssetAsync(withEnglishSubtitle: false);
        await DrainAsync();

        // Re-run the search service directly: the language is now satisfied, so nothing new happens.
        await using (var scope = _host.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ISubtitleSearch>().SearchForAssetAsync(assetId);
        }

        await DrainAsync();
        Assert.Single(await QuerySubtitlesAsync(q => q.ListForAssetAsync(assetId)));
    }

    [Fact]
    public async Task A_failed_download_is_recorded_and_not_retried_before_its_backoff()
    {
        _provider.DownloadFailure = new HttpRequestException("down");
        try
        {
            var assetId = await RegisterAssetAsync(withEnglishSubtitle: false);
            await DrainAsync();

            var search = Assert.Single(await QuerySubtitlesAsync(q => q.ListForAssetAsync(assetId)));
            Assert.Equal(SubtitleSearchState.NotFound, search.State);
            Assert.Equal(1, search.Attempts);
            Assert.Empty(_fileStore.Written);

            await using (var scope = _host.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<ISubtitleSearch>().SearchForAssetAsync(assetId);
            }

            search = Assert.Single(await QuerySubtitlesAsync(q => q.ListForAssetAsync(assetId)));
            Assert.Equal(1, search.Attempts);
        }
        finally
        {
            _provider.DownloadFailure = null;
        }
    }

    // -- helpers ---------------------------------------------------------------------------------

    private async Task<Guid> RegisterAssetAsync(bool withEnglishSubtitle, string path = MoviePath)
    {
        var assetId = Uuid7.New();
        var streams = new List<MediaStreamInput>
        {
            new(0, MediaStreamType.Video, "h264", null, null, 1920, 1080, null, null, true, false),
            new(1, MediaStreamType.Audio, "aac", "eng", 6, null, null, null, null, true, false),
        };
        if (withEnglishSubtitle)
        {
            streams.Add(new MediaStreamInput(2, MediaStreamType.Subtitle, "subrip", "en", null, null, null, null, null, false, false));
        }

        var request = new RegisterMediaAssetRequest(
            assetId, await AddWorkAsync(), [Uuid7.New()], path, 2_000_000_000, "matroska", streams);

        await using var scope = _host.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ILibraryCommands>().RegisterMediaAssetAsync(request);
        return assetId;
    }

    private async Task<T> QuerySubtitlesAsync<T>(Func<ISubtitleQuery, Task<T>> query)
    {
        await using var scope = _host.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<ISubtitleQuery>());
    }

    private async Task<T> QueryLibraryAsync<T>(Func<ILibraryQuery, Task<T>> query)
    {
        await using var scope = _host.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<ILibraryQuery>());
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
        await using var scope = _host.CreateAsyncScope();
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
        await using var scope = _host.CreateAsyncScope();
        var relay = scope.ServiceProvider.GetRequiredService<OutboxRelay>();
        var total = 0;
        int published;
        while ((published = await relay.ProcessBatchAsync()) > 0)
        {
            total += published;
        }

        return total;
    }

    /// <summary>A real catalogued work in the default (open) collection — a search is visible when its work is.</summary>
    private async Task<Guid> AddWorkAsync()
    {
        await using var scope = _host.CreateAsyncScope();
        var added = await scope.ServiceProvider.GetRequiredService<ICatalogCommands>()
            .AddMovieAsync("The Matrix", 1999, []);
        return added.Value.Value;
    }
}
