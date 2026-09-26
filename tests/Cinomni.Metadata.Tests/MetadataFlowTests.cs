using Cinomni.Catalog.Contracts;
using Cinomni.Metadata.Contracts;
using Cinomni.Metadata.Messaging;
using Cinomni.Metadata.Persistence;
using Cinomni.Operations.Messaging;
using Cinomni.Operations.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Metadata.Tests;

/// <summary>
/// Integration tests for the metadata ACL spine against a real PostgreSQL instance: a RefreshMetadata
/// command fetches a (faked) snapshot, selects its artwork, persists it and emits MetadataRefreshed,
/// which flows to Catalog so it enriches the work with the neutral fields and artwork (identity
/// unchanged). Also covers multi-provider search routing, provider degradation, and the
/// artwork override. Events are driven by alternately draining the outbox and the command queue.
/// </summary>
public sealed class MetadataFlowTests : IAsyncLifetime
{
    private const string ExternalId = "603";

    private readonly FakeMetadataSource _source = new();
    private ServiceProvider _host = null!;

    public async Task InitializeAsync() => _host = await MetadataTestHost.CreateAsync("cinomni_test_metadata", [_source]);

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Refreshing_enriches_the_catalog_work()
    {
        var workId = await AddMovieAsync("placeholder", year: null);

        await RefreshAsync(workId);
        await DrainAsync();

        // The work was enriched with the provider's canonical title, year and selected poster (identity unchanged).
        var work = await GetWorkAsync(workId);
        Assert.Equal("The Matrix", work!.Title);
        Assert.Equal(1999, work.Year);
        Assert.Equal(FakeMetadataSource.PosterEnUrl, work.PosterUrl);
        Assert.Equal(FakeMetadataSource.BackdropUrl, work.BackdropUrl);

        // A single snapshot landed for the work.
        Assert.Equal(1, await CountSnapshotsAsync(workId));
        Assert.Equal(MetadataRefreshStatus.Fresh, await RefreshStatusAsync(workId));
    }

    [Fact]
    public async Task Refreshing_selects_and_persists_every_artwork_candidate()
    {
        var workId = await AddMovieAsync("placeholder", year: null);

        await RefreshAsync(workId);
        await DrainAsync();

        var snapshot = await GetSnapshotForWorkAsync(workId);
        Assert.Equal(3, snapshot!.Artwork.Count); // every candidate persisted

        // Exactly one poster is selected — the English one, on language priority over the higher-voted Spanish.
        var selectedPoster = Assert.Single(snapshot.Artwork, a => a is { Kind: ArtworkKind.Poster, IsSelected: true });
        Assert.Equal(FakeMetadataSource.PosterEnUrl, selectedPoster.Url);
    }

    [Fact]
    public async Task A_provider_failure_marks_failed_and_leaves_the_work_unchanged()
    {
        _source.Result = null; // provider unavailable
        var workId = await AddMovieAsync("placeholder", year: null);

        await RefreshAsync(workId);
        await DrainAsync();

        var work = await GetWorkAsync(workId);
        Assert.Equal("placeholder", work!.Title);
        Assert.Null(work.Year);
        Assert.Null(work.PosterUrl);

        Assert.Equal(0, await CountSnapshotsAsync(workId));
        Assert.Equal(MetadataRefreshStatus.Failed, await RefreshStatusAsync(workId));
    }

    [Fact]
    public async Task A_provider_answering_malformed_json_backs_off_like_any_other_failure()
    {
        // A JsonException escaped the refresh: no backoff, no MetadataRefreshFailed, and the queue
        // retried the command at once.
        _source.FetchException = new System.Text.Json.JsonException("not the expected document");
        var workId = await AddMovieAsync("placeholder", year: null);

        await RefreshAsync(workId);
        await DrainAsync();

        Assert.Equal(MetadataRefreshStatus.Failed, await RefreshStatusAsync(workId));
        Assert.Equal(1, await CountOutboxAsync(_host, MetadataEventNames.MetadataRefreshFailed));
    }

    [Fact]
    public async Task Refreshing_twice_within_the_ttl_fetches_once()
    {
        var workId = await AddMovieAsync("placeholder", year: null);

        await RefreshAsync(workId);
        await DrainAsync();
        await RefreshAsync(workId); // second trigger: fresh within the TTL, so a no-op
        await DrainAsync();

        Assert.Equal(1, await CountSnapshotsAsync(workId));
        Assert.Single(_source.Fetched);
    }

    [Fact]
    public async Task Overriding_artwork_updates_the_work()
    {
        var workId = await AddMovieAsync("placeholder", year: null);
        await RefreshAsync(workId);
        await DrainAsync();

        var snapshot = await GetSnapshotForWorkAsync(workId);
        var spanishPoster = snapshot!.Artwork.Single(a => a.Url == FakeMetadataSource.PosterEsUrl);

        var result = await SelectArtworkAsync(snapshot.Id, spanishPoster.Id);
        Assert.True(result.IsSuccess);
        await DrainAsync();

        // The override re-pointed the selected poster and propagated it to the work.
        var work = await GetWorkAsync(workId);
        Assert.Equal(FakeMetadataSource.PosterEsUrl, work!.PosterUrl);

        var updated = await GetSnapshotForWorkAsync(workId);
        Assert.Equal(FakeMetadataSource.PosterEsUrl, updated!.PosterUrl);
        Assert.True(updated.Artwork.Single(a => a.Url == FakeMetadataSource.PosterEsUrl).IsSelected);
        Assert.False(updated.Artwork.Single(a => a.Url == FakeMetadataSource.PosterEnUrl).IsSelected);
    }

    [Fact]
    public async Task Re_selecting_a_previously_used_artwork_still_propagates()
    {
        var workId = await AddMovieAsync("placeholder", year: null);
        await RefreshAsync(workId);
        await DrainAsync();

        var snapshot = await GetSnapshotForWorkAsync(workId);
        var spanish = snapshot!.Artwork.Single(a => a.Url == FakeMetadataSource.PosterEsUrl).Id;
        var english = snapshot.Artwork.Single(a => a.Url == FakeMetadataSource.PosterEnUrl).Id;

        // English is auto-selected. Bounce es -> en -> es: the final re-pick reuses a value used before,
        // which a value-based idempotency key would silently drop (leaving Catalog stuck on en).
        Assert.True((await SelectArtworkAsync(snapshot.Id, spanish)).IsSuccess);
        await DrainAsync();
        Assert.True((await SelectArtworkAsync(snapshot.Id, english)).IsSuccess);
        await DrainAsync();
        Assert.True((await SelectArtworkAsync(snapshot.Id, spanish)).IsSuccess);
        await DrainAsync();

        var work = await GetWorkAsync(workId);
        Assert.Equal(FakeMetadataSource.PosterEsUrl, work!.PosterUrl);
    }

    [Fact]
    public async Task Search_returns_provider_candidates()
    {
        var candidates = await SearchAsync("matrix");

        var candidate = Assert.Single(candidates);
        Assert.Equal("tmdb", candidate.Provider);
        Assert.Equal(ExternalId, candidate.ExternalId);
        Assert.Equal("The Matrix", candidate.Title);
    }

    [Fact]
    public async Task Search_orders_by_priority_and_filters_by_kind()
    {
        var tmdb = new FakeMetadataSource("tmdb", MetadataMediaKind.Movie)
        {
            Candidates = [new("1", "From TMDB", 1999, null)],
        };
        var tvdb = new FakeMetadataSource("tvdb", MetadataMediaKind.Movie)
        {
            Candidates = [new("2", "From TVDB", 1999, null)],
        };
        var tvmaze = new FakeMetadataSource("tvmaze", MetadataMediaKind.Series)
        {
            Candidates = [new("3", "From TVMaze", 1999, null)],
        };

        // Registered TVDB-first to prove ordering follows priority, not registration order.
        await using var host = await MetadataTestHost.CreateAsync("cinomni_test_metadata_multi", [tvdb, tmdb, tvmaze]);

        await using var scope = host.CreateAsyncScope();
        var results = await scope.ServiceProvider.GetRequiredService<IMetadataSearch>()
            .SearchAsync("matrix", year: null, MetadataMediaKind.Movie);

        // TVMaze (series-only) is excluded; the movie providers come back in priority order.
        Assert.True(results.IsSuccess, results.Error.Message);
        Assert.Collection(
            results.Value,
            first => Assert.Equal("tmdb", first.Provider),
            second => Assert.Equal("tvdb", second.Provider));
    }

    [Fact]
    public async Task A_persistently_failing_provider_is_announced_degraded()
    {
        var source = new FakeMetadataSource { Result = null }; // always unavailable
        await using var host = await MetadataTestHost.CreateAsync(
            "cinomni_test_metadata_degraded", [source], options => options.DegradeAfterAttempts = 1);

        var workId = await AddMovieAsync(host, "placeholder");
        await EnqueueRefreshAsync(host, workId);
        await DrainAsync(host);

        Assert.Equal(1, await CountOutboxAsync(host, MetadataEventNames.ProviderDegraded));
    }

    // -- helpers ---------------------------------------------------------------------------------

    private Task<Guid> AddMovieAsync(string title, int? year) => AddMovieAsync(_host, title, year);

    private static async Task<Guid> AddMovieAsync(IServiceProvider host, string title, int? year = null)
    {
        await using var scope = host.CreateAsyncScope();
        var commands = scope.ServiceProvider.GetRequiredService<ICatalogCommands>();
        var result = await commands.AddMovieAsync(title, year, [new ExternalId(MetadataProvider.Tmdb, ExternalId)]);
        return result.Value.Value;
    }

    private Task RefreshAsync(Guid workId) => EnqueueRefreshAsync(_host, workId);

    private static async Task EnqueueRefreshAsync(IServiceProvider host, Guid workId)
    {
        await using var scope = host.CreateAsyncScope();
        var queue = scope.ServiceProvider.GetRequiredService<ICommandQueue>();
        // A distinct key per call so each trigger enqueues (the service, not the queue, decides no-op).
        await queue.EnqueueAsync(
            new RefreshMetadataCommand(workId, "tmdb", ExternalId),
            idempotencyKey: $"test-refresh:{workId}:{Guid.NewGuid()}");
    }

    private async Task<WorkSummary?> GetWorkAsync(Guid workId)
    {
        await using var scope = _host.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ICatalogQuery>().GetByIdAsync(new WorkId(workId));
    }

    private async Task<MetadataSnapshot?> GetSnapshotForWorkAsync(Guid workId)
    {
        await using var scope = _host.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MetadataDbContext>();
        var snapshotId = await dbContext.Snapshots.Where(s => s.WorkId == workId).Select(s => s.Id).FirstOrDefaultAsync();
        if (snapshotId == Guid.Empty)
        {
            return null;
        }

        return await scope.ServiceProvider.GetRequiredService<IMetadataQuery>()
            .GetSnapshotAsync(new MetadataSnapshotId(snapshotId));
    }

    private async Task<Cinomni.Kernel.Results.Result> SelectArtworkAsync(MetadataSnapshotId snapshotId, Guid artworkId)
    {
        await using var scope = _host.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IMetadataArtwork>().SelectAsync(snapshotId, artworkId);
    }

    private async Task<IReadOnlyList<MetadataCandidate>> SearchAsync(string term)
    {
        await using var scope = _host.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<IMetadataSearch>().SearchAsync(term, year: null);
        Assert.True(result.IsSuccess, result.Error.Message);
        return result.Value;
    }

    private async Task<int> CountSnapshotsAsync(Guid workId)
    {
        await using var scope = _host.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<MetadataDbContext>()
            .Snapshots.CountAsync(s => s.WorkId == workId);
    }

    private async Task<MetadataRefreshStatus> RefreshStatusAsync(Guid workId)
    {
        await using var scope = _host.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<MetadataDbContext>()
            .RefreshStates.Where(s => s.WorkId == workId).Select(s => s.Status).FirstAsync();
    }

    private static async Task<int> CountOutboxAsync(IServiceProvider host, string eventType)
    {
        await using var scope = host.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OperationsDbContext>()
            .Outbox.CountAsync(m => m.EventType == eventType);
    }

    private Task DrainAsync() => DrainAsync(_host);

    private static async Task DrainAsync(IServiceProvider host)
    {
        while (true)
        {
            var commands = await DrainCommandsAsync(host);
            var events = await DrainOutboxAsync(host);
            if (commands == 0 && events == 0)
            {
                break;
            }
        }
    }

    private static async Task<int> DrainCommandsAsync(IServiceProvider host)
    {
        await using var scope = host.CreateAsyncScope();
        var processor = scope.ServiceProvider.GetRequiredService<CommandProcessor>();
        var total = 0;
        int processed;
        while ((processed = await processor.ProcessBatchAsync()) > 0)
        {
            total += processed;
        }

        return total;
    }

    private static async Task<int> DrainOutboxAsync(IServiceProvider host)
    {
        await using var scope = host.CreateAsyncScope();
        var relay = scope.ServiceProvider.GetRequiredService<OutboxRelay>();
        var total = 0;
        int published;
        while ((published = await relay.ProcessBatchAsync()) > 0)
        {
            total += published;
        }

        return total;
    }
}
