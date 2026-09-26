using Cinomni.Kernel.Messaging;
using Cinomni.Metadata.Contracts;
using Cinomni.Metadata.Messaging;
using Cinomni.Metadata.Persistence;
using Cinomni.Metadata.Providers;
using Cinomni.Operations;
using Cinomni.Operations.Messaging;
using Cinomni.Operations.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Metadata.Tests;

/// <summary>
/// Integration tests for the series refresh orchestration against a real PostgreSQL instance: the
/// structure lands in the same unit of work as <c>MetadataRefreshed</c>, the short TTL applies while a
/// show is still producing episodes, a re-refresh leaves no orphaned children, and the continuing-series
/// sweep enqueues the work a hand-posted refresh would otherwise be the only source of.
/// <para>
/// Every host name here is distinct from every other suite's: each test host opens with
/// <c>EnsureDeletedAsync</c> on a hard-coded database and <c>dotnet test</c> parallelises across
/// projects, so a shared name drops another suite's database mid-run.
/// </para>
/// </summary>
public sealed class MetadataSeriesFlowTests : IAsyncLifetime
{
    private const string Provider = "tvdb";

    private readonly FakeMetadataSource _source = FakeMetadataSource.ForSeries(Provider);
    private ServiceProvider _host = null!;

    public async Task InitializeAsync() =>
        _host = await MetadataTestHost.CreateAsync("cinomni_test_metadata_series_flow", [_source]);

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task A_series_refresh_persists_the_structure_and_publishes_refreshed_atomically()
    {
        var workId = Guid.NewGuid();

        await EnqueueRefreshAsync(_host, workId);
        await DrainAsync(_host);

        await using var scope = _host.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MetadataDbContext>();
        var snapshot = await dbContext.Snapshots.AsNoTracking().SingleAsync(s => s.WorkId == workId);

        Assert.Equal(MetadataMediaKind.Series, snapshot.Kind);
        Assert.Equal(SeriesStatus.Continuing, snapshot.SeriesStatus);
        Assert.Equal(new DateOnly(2002, 6, 2), snapshot.FirstAired);
        Assert.Equal("121361", snapshot.TvdbId);
        Assert.Equal("tt0306414", snapshot.ImdbId);
        Assert.Equal(SeasonOrders.Official, snapshot.SeasonOrder);

        // Children cascaded with the snapshot — same SaveChanges, same transaction as the event.
        Assert.Equal(2, await dbContext.Seasons.CountAsync(s => s.SnapshotId == snapshot.Id));
        Assert.Equal(4, await dbContext.Episodes.CountAsync(e => e.SnapshotId == snapshot.Id));

        var structure = await scope.ServiceProvider.GetRequiredService<IMetadataQuery>()
            .GetSeriesStructureAsync(new MetadataSnapshotId(snapshot.Id));
        var premiere = structure!.Episodes.Single(e => e is { SeasonNumber: 1, Number: 1 });
        Assert.Equal(1, premiere.AbsoluteNumber);
        Assert.Equal(new DateTimeOffset(2002, 6, 3, 1, 0, 0, TimeSpan.Zero), premiere.AirDateTime);
        Assert.True(structure.Episodes.Single(e => e.SeasonNumber == 0).IsSpecial);

        // The event that carries the snapshot downstream was published in the same commit.
        Assert.Equal(1, await CountOutboxAsync(_host, MetadataEventNames.MetadataRefreshed));
    }

    [Fact]
    public async Task A_series_refresh_scopes_the_season_poster_and_the_episode_still()
    {
        var workId = Guid.NewGuid();

        await EnqueueRefreshAsync(_host, workId);
        await DrainAsync(_host);

        await using var scope = _host.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MetadataDbContext>();
        var snapshot = await dbContext.Snapshots.AsNoTracking()
            .Include(s => s.Artwork)
            .SingleAsync(s => s.WorkId == workId);

        // The series poster is still the English one: a season poster in the same flat list would
        // otherwise be a fourth Poster candidate competing for the one series slot.
        Assert.Equal(FakeMetadataSource.PosterEnUrl, snapshot.PosterUrl);
        Assert.Equal(FakeMetadataSource.BackdropUrl, snapshot.BackdropUrl);

        var seasonPoster = Assert.Single(snapshot.Artwork, a => a.SeasonNumber == 1 && a.EpisodeNumber == null);
        Assert.Equal(FakeMetadataSource.SeasonOnePosterUrl, seasonPoster.Url);
        Assert.True(seasonPoster.IsSelected); // the only candidate in its own scope

        var still = Assert.Single(snapshot.Artwork, a => a.Kind == ArtworkKind.Still);
        Assert.Equal(FakeMetadataSource.EpisodeStillUrl, still.Url);
        Assert.Equal(1, still.SeasonNumber);
        Assert.Equal(1, still.EpisodeNumber);
        Assert.True(still.IsSelected);

        // Exactly one series-level poster is selected, and it is not the season's.
        var seriesPosters = snapshot.Artwork.Where(a => a is { Kind: ArtworkKind.Poster, SeasonNumber: null }).ToList();
        Assert.Equal(2, seriesPosters.Count);
        Assert.Equal(FakeMetadataSource.PosterEnUrl, Assert.Single(seriesPosters, a => a.IsSelected).Url);
    }

    [Fact]
    public async Task A_second_refresh_within_the_series_ttl_is_skipped()
    {
        var workId = Guid.NewGuid();

        await EnqueueRefreshAsync(_host, workId);
        await DrainAsync(_host);
        await EnqueueRefreshAsync(_host, workId);
        await DrainAsync(_host);

        Assert.Single(_source.Fetched);
        await using var scope = _host.CreateAsyncScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<MetadataDbContext>()
            .Snapshots.CountAsync(s => s.WorkId == workId));
    }

    [Fact]
    public async Task A_continuing_series_uses_the_short_ttl()
    {
        // One host, two works, one option set: the only difference is the status the provider reported.
        // With the series TTL elapsed and the standard TTL not, only the continuing show re-fetches.
        var continuing = FakeMetadataSource.ForSeries(Provider);
        var ended = FakeMetadataSource.ForSeries("tvmaze", SeriesStatus.Ended);
        await using var host = await MetadataTestHost.CreateAsync(
            "cinomni_test_metadata_series_ttl",
            [continuing, ended],
            options =>
            {
                options.RefreshTtl = TimeSpan.FromDays(7);
                options.SeriesRefreshTtl = TimeSpan.Zero;
            });

        var continuingWork = Guid.NewGuid();
        var endedWork = Guid.NewGuid();

        await EnqueueRefreshAsync(host, continuingWork);
        await EnqueueRefreshAsync(host, endedWork, "tvmaze");
        await DrainAsync(host);

        await EnqueueRefreshAsync(host, continuingWork);
        await EnqueueRefreshAsync(host, endedWork, "tvmaze");
        await DrainAsync(host);

        Assert.Equal(2, continuing.Fetched.Count);
        Assert.Single(ended.Fetched);
    }

    [Fact]
    public async Task Refreshing_a_series_again_replaces_the_structure_without_orphans()
    {
        var trimmed = FakeMetadataSource.SeriesResult(
            SeriesStatus.Continuing,
            seasons: [FakeMetadataSource.DefaultSeasons[0]],
            episodes: [FakeMetadataSource.DefaultEpisodes[1]]);

        var source = FakeMetadataSource.ForSeries(Provider);
        await using var host = await MetadataTestHost.CreateAsync(
            "cinomni_test_metadata_series_replace", [source], options => options.SeriesRefreshTtl = TimeSpan.Zero);

        var workId = Guid.NewGuid();
        await EnqueueRefreshAsync(host, workId);
        await DrainAsync(host);

        // The provider drops a season and three episodes (a renumbering, or a correction).
        source.Result = trimmed;
        await EnqueueRefreshAsync(host, workId);
        await DrainAsync(host);

        await using var scope = host.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MetadataDbContext>();

        var snapshots = await dbContext.Snapshots.AsNoTracking()
            .Where(s => s.WorkId == workId)
            .Select(s => s.Id)
            .ToListAsync();
        Assert.Equal(2, snapshots.Count);

        // The refresh state points at the snapshot the last fetch produced — the authoritative "current"
        // one, without depending on two timestamps taken milliseconds apart.
        var currentId = await dbContext.RefreshStates.AsNoTracking()
            .Where(s => s.WorkId == workId && s.Provider == Provider)
            .Select(s => s.SnapshotId)
            .SingleAsync();
        var previousId = snapshots.Single(id => id != currentId);

        // Each snapshot owns its own structure: the newest describes the trimmed series, the older one
        // is untouched, and nothing points at a snapshot that is gone.
        Assert.Equal(1, await dbContext.Seasons.CountAsync(s => s.SnapshotId == currentId));
        Assert.Equal(1, await dbContext.Episodes.CountAsync(e => e.SnapshotId == currentId));
        Assert.Equal(2, await dbContext.Seasons.CountAsync(s => s.SnapshotId == previousId));
        Assert.Equal(4, await dbContext.Episodes.CountAsync(e => e.SnapshotId == previousId));

        var liveSnapshots = await dbContext.Snapshots.AsNoTracking().Select(s => s.Id).ToListAsync();
        Assert.Empty(await dbContext.Episodes.AsNoTracking()
            .Where(e => !liveSnapshots.Contains(e.SnapshotId)).ToListAsync());
    }

    [Fact]
    public async Task A_provider_that_lists_a_season_twice_does_not_roll_back_the_refresh()
    {
        // (snapshot, season, number) is a unique index: one duplicated row from a provider would
        // otherwise take the snapshot, its whole structure and the event down with it.
        var duplicated = FakeMetadataSource.SeriesResult(
            SeriesStatus.Continuing,
            seasons: [FakeMetadataSource.DefaultSeasons[0], FakeMetadataSource.DefaultSeasons[0]],
            episodes: [FakeMetadataSource.DefaultEpisodes[1], FakeMetadataSource.DefaultEpisodes[1]]);

        var source = FakeMetadataSource.ForSeries(Provider);
        source.Result = duplicated;
        await using var host = await MetadataTestHost.CreateAsync("cinomni_test_metadata_series_dupes", [source]);

        var workId = Guid.NewGuid();
        await EnqueueRefreshAsync(host, workId);
        await DrainAsync(host);

        await using var scope = host.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MetadataDbContext>();
        var snapshot = await dbContext.Snapshots.AsNoTracking().SingleAsync(s => s.WorkId == workId);
        Assert.Equal(1, await dbContext.Seasons.CountAsync(s => s.SnapshotId == snapshot.Id));
        Assert.Equal(1, await dbContext.Episodes.CountAsync(e => e.SnapshotId == snapshot.Id));
    }

    [Fact]
    public async Task The_continuing_series_sweep_enqueues_a_refresh_and_leaves_ended_shows_alone()
    {
        var continuing = FakeMetadataSource.ForSeries(Provider);
        var ended = FakeMetadataSource.ForSeries("tvmaze", SeriesStatus.Ended);
        await using var host = await MetadataTestHost.CreateAsync(
            "cinomni_test_metadata_series_sweep",
            [continuing, ended],
            options => options.SeriesRefreshTtl = TimeSpan.Zero);

        var continuingWork = Guid.NewGuid();
        var endedWork = Guid.NewGuid();
        await EnqueueRefreshAsync(host, continuingWork);
        await EnqueueRefreshAsync(host, endedWork, "tvmaze");
        await DrainAsync(host);

        // Hosted services never start under a test ServiceProvider, so the job's command is run directly.
        await using (var scope = host.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<RefreshContinuingSeriesCommand>>();
            Assert.True((await handler.HandleAsync(new RefreshContinuingSeriesCommand())).IsSuccess);
        }

        await DrainAsync(host);

        Assert.Equal(2, continuing.Fetched.Count); // swept and re-fetched
        Assert.Single(ended.Fetched); // concluded: nothing new can air
    }

    [Fact]
    public async Task The_sweep_does_not_re_enqueue_within_the_same_window()
    {
        var source = FakeMetadataSource.ForSeries(Provider);
        await using var host = await MetadataTestHost.CreateAsync(
            "cinomni_test_metadata_series_sweep_twice", [source], options => options.SeriesRefreshTtl = TimeSpan.Zero);

        var workId = Guid.NewGuid();
        await EnqueueRefreshAsync(host, workId);
        await DrainAsync(host);

        await SweepAsync(host);
        await SweepAsync(host);
        await DrainAsync(host);

        // operations.command's unique idempotency key is consumed forever, so the hour window is what
        // keeps the sweep repeatable without letting one tick enqueue the same work twice.
        Assert.Equal(2, source.Fetched.Count);
    }

    // -- helpers ---------------------------------------------------------------------------------

    private static async Task SweepAsync(IServiceProvider host)
    {
        await using var scope = host.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ICommandHandler<RefreshContinuingSeriesCommand>>()
            .HandleAsync(new RefreshContinuingSeriesCommand());
    }

    private static async Task EnqueueRefreshAsync(IServiceProvider host, Guid workId, string provider = Provider)
    {
        await using var scope = host.CreateAsyncScope();
        // A distinct key per call so each trigger enqueues (the service, not the queue, decides no-op).
        await scope.ServiceProvider.GetRequiredService<ICommandQueue>().EnqueueAsync(
            new RefreshMetadataCommand(workId, provider, FakeMetadataSource.SeriesExternalId, MetadataMediaKind.Series),
            idempotencyKey: $"test-series-refresh:{workId}:{Guid.NewGuid()}");
    }

    private static async Task<int> CountOutboxAsync(IServiceProvider host, string eventType)
    {
        await using var scope = host.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OperationsDbContext>()
            .Outbox.CountAsync(m => m.EventType == eventType);
    }

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
