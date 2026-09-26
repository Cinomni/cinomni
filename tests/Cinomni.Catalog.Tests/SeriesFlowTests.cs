using Cinomni.Catalog.Contracts;
using Cinomni.Catalog.Messaging;
using Cinomni.Catalog.Persistence;
using Cinomni.Import.Contracts;
using Cinomni.Kernel.Identifiers;
using Cinomni.Kernel.Messaging;
using Cinomni.Metadata.Contracts;
using Cinomni.Operations.Messaging;
using Cinomni.Operations.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Catalog.Tests;

/// <summary>
/// Integration tests for the Catalog series write path against a real PostgreSQL instance: adding a
/// series, materialising a provider's structure without ever deleting a row, and the per-unit
/// availability fan-out an import drives.
/// <para>
/// Runs against its own database — every <c>TestHost</c> opens with <c>EnsureDeletedAsync</c> and xUnit
/// parallelises test classes, so sharing a name with another suite drops its database mid-run.
/// </para>
/// </summary>
public sealed class SeriesFlowTests : IAsyncLifetime
{
    private const string Database = "cinomni_test_catalog_write";
    private const string ClaimingProvider = "tvdb";

    private readonly FakeMetadataQuery _metadata = new();
    private readonly CatalogEventSink _sink = new();
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync() =>
        _provider = await CatalogTestHost.CreateAsync(Database, _sink.Register, _metadata);

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task Adding_a_series_publishes_one_work_added_with_kind_series()
    {
        var workId = await AddSeriesAsync("The Wire", 2002, [new ExternalId(MetadataProvider.Tvdb, "79126")]);

        // Exactly ONE outbox message, the same event a movie publishes: consumers need no new
        // subscription and the movie "one message per add" assertion still holds.
        await using (var scope = _provider.CreateAsyncScope())
        {
            var operationsDb = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
            var message = await operationsDb.Outbox.SingleAsync();
            Assert.Equal(CatalogEventNames.WorkAdded, message.EventType);
        }

        await DrainAsync();

        var added = Assert.Single(_sink.WorkAdded);
        Assert.Equal(workId, added.WorkId);
        Assert.Equal("Series", added.Kind);
    }

    [Fact]
    public async Task Adding_the_same_tvdb_id_twice_returns_the_existing_series()
    {
        var first = await AddSeriesAsync("Breaking Bad", 2008, [new ExternalId(MetadataProvider.Tvdb, "81189")]);
        var second = await AddSeriesAsync("Breaking Bad (dup)", 2008, [new ExternalId(MetadataProvider.Tvdb, "81189")]);

        // Rule 16 is shared with the movie path, not re-implemented: same identity, one work.
        Assert.Equal(first, second);
        await using var scope = _provider.CreateAsyncScope();
        var query = scope.ServiceProvider.GetRequiredService<ICatalogQuery>();
        Assert.Single(await query.ListAsync());
    }

    [Fact]
    public async Task Syncing_structure_creates_seasons_and_episodes()
    {
        var workId = await AddSeriesAsync("Deadwood", 2004, []);
        var snapshotId = _metadata.AddSeries(
            workId, ClaimingProvider, "Deadwood",
            [Season(1, "Season 1"), Season(2, "Season 2")],
            [Episode(1, 1), Episode(1, 2), Episode(2, 1)]);

        // Driven the way production does it: MetadataRefreshed → command queue → structure handler.
        await RefreshAsync(workId, snapshotId);

        Assert.Equal(2, await CountSeasonsAsync(workId));
        Assert.Equal(3, await CountEpisodesAsync(workId));

        var work = await WorkAsync(workId);
        Assert.Equal(ClaimingProvider, work.StructureProvider);
        Assert.Equal(3, work.EpisodeCount);

        var announced = Assert.Single(_sink.StructureChanged);
        Assert.Equal(snapshotId.Value, announced.SnapshotId);
        Assert.Equal(2, announced.SeasonCount);
        Assert.Equal(3, announced.EpisodeCount);
        Assert.Equal(3, announced.CreatedEpisodeIds.Count);
    }

    [Fact]
    public async Task Syncing_the_same_snapshot_twice_changes_nothing()
    {
        var workId = await AddSeriesAsync("Mad Men", 2007, []);
        var snapshotId = _metadata.AddSeries(
            workId, ClaimingProvider, "Mad Men", [Season(1)], [Episode(1, 1), Episode(1, 2)]);

        await SyncStructureAsync(workId, snapshotId);
        // The command queue is at-least-once, so the handler must survive a second delivery.
        await SyncStructureAsync(workId, snapshotId);

        Assert.Equal(1, await CountSeasonsAsync(workId));
        Assert.Equal(2, await CountEpisodesAsync(workId));
        Assert.Equal(2, (await WorkAsync(workId)).EpisodeCount);

        await DrainAsync();
        Assert.Equal(2, _sink.StructureChanged.Count);
        // Both passes report the same totals; only the first one created anything.
        Assert.All(_sink.StructureChanged, changed => Assert.Equal(2, changed.EpisodeCount));
        Assert.Single(_sink.StructureChanged, changed => changed.CreatedEpisodeIds.Count == 2);
        Assert.Single(_sink.StructureChanged, changed => changed.CreatedEpisodeIds.Count == 0);
    }

    [Fact]
    public async Task An_episode_dropped_by_a_later_snapshot_but_flagged_available_survives()
    {
        var workId = await AddSeriesAsync("The Sopranos", 1999, []);
        var first = _metadata.AddSeries(
            workId, ClaimingProvider, "The Sopranos", [Season(1)],
            [Episode(1, 1), Episode(1, 2), Episode(1, 3)]);
        await SyncStructureAsync(workId, first);

        var episodeId = await EpisodeIdAsync(workId, 1, 2);
        await MarkEpisodeAvailableAsync(episodeId, Uuid7.New(), Uuid7.New());

        // A later snapshot no longer lists S01E02 at all — a provider renumbering specials looks exactly
        // like this. A delete-then-insert here would orphan the asset link pointing at that episode id.
        var second = _metadata.AddSeries(
            workId, ClaimingProvider, "The Sopranos", [Season(1)], [Episode(1, 1), Episode(1, 3)]);
        await SyncStructureAsync(workId, second);

        var survivor = await EpisodeAsync(episodeId);
        Assert.NotNull(survivor);
        Assert.True(survivor!.HasAsset);
        Assert.Equal(3, await CountEpisodesAsync(workId));
    }

    [Fact]
    public async Task A_provider_shifting_every_absolute_number_settles_in_one_sync()
    {
        var workId = await AddSeriesAsync("Bleach", 2004, []);
        await SyncStructureAsync(workId, AbsoluteSnapshotAsync(workId, [1, 2, 3, 4]));

        // TheTVDB inserts a recap special into the absolute ordering, so the whole ordering shifts by one.
        // Every episode still owns a number another episode holds right now.
        await SyncStructureAsync(workId, AbsoluteSnapshotAsync(workId, [2, 3, 4, 5]));

        // One sync, not four hundred: the catalog agrees with the provider immediately, so an anime
        // release numbered by the provider's ordering resolves to the episode the provider means.
        Assert.Equal([2, 3, 4, 5], await AbsoluteNumbersAsync(workId));
    }

    [Fact]
    public async Task Two_episodes_swapping_absolute_numbers_settle_in_one_sync()
    {
        var workId = await AddSeriesAsync("Naruto", 2002, []);
        await SyncStructureAsync(workId, AbsoluteSnapshotAsync(workId, [1, 2]));

        await SyncStructureAsync(workId, AbsoluteSnapshotAsync(workId, [2, 1]));

        Assert.Equal([2, 1], await AbsoluteNumbersAsync(workId));
    }

    [Fact]
    public async Task An_absolute_number_handed_to_another_episode_is_released_by_its_old_owner()
    {
        var workId = await AddSeriesAsync("One Piece", 1999, []);
        await SyncStructureAsync(workId, AbsoluteSnapshotAsync(workId, [1, 2]));

        // The snapshot no longer lists S01E01 at all and gives its absolute number to S01E02: the stale
        // claim must be released, because nothing else would ever free it.
        await SyncStructureAsync(
            workId,
            _metadata.AddSeries(workId, ClaimingProvider, "One Piece", [Season(1)], [Episode(1, 2, absoluteNumber: 1)]));

        Assert.Equal([null, 1], await AbsoluteNumbersAsync(workId));
        Assert.Equal(2, await CountEpisodesAsync(workId)); // still never deleted
    }

    [Fact]
    public async Task Re_syncing_the_same_absolute_numbers_leaves_them_untouched()
    {
        var workId = await AddSeriesAsync("Gintama", 2006, []);
        await SyncStructureAsync(workId, AbsoluteSnapshotAsync(workId, [1, 2, 3]));

        await SyncStructureAsync(workId, AbsoluteSnapshotAsync(workId, [1, 2, 3]));

        Assert.Equal([1, 2, 3], await AbsoluteNumbersAsync(workId));
    }

    [Fact]
    public async Task A_snapshot_that_supplies_no_absolute_number_keeps_the_stored_one()
    {
        var workId = await AddSeriesAsync("Monster", 2004, []);
        await SyncStructureAsync(workId, AbsoluteSnapshotAsync(workId, [1, 2]));

        // The only-overwrite-when-supplied rule: a provider that stops publishing absolute numbers must
        // not blank the ones anime releases already match against.
        await SyncStructureAsync(
            workId,
            _metadata.AddSeries(workId, ClaimingProvider, "Monster", [Season(1)], [Episode(1, 1), Episode(1, 2)]));

        Assert.Equal([1, 2], await AbsoluteNumbersAsync(workId));
    }

    [Fact]
    public async Task An_empty_snapshot_does_not_wipe_the_structure()
    {
        var workId = await AddSeriesAsync("Rome", 2005, []);
        var snapshotId = _metadata.AddSeries(
            workId, ClaimingProvider, "Rome", [Season(1)], [Episode(1, 1), Episode(1, 2)]);
        await SyncStructureAsync(workId, snapshotId);

        // A provider outage that answers with nothing must leave the catalog exactly as it was.
        await using (var scope = _provider.CreateAsyncScope())
        {
            var commands = scope.ServiceProvider.GetRequiredService<ICatalogCommands>();
            await commands.SyncSeriesStructureAsync(
                workId, new SeriesStructure(Uuid7.New(), ClaimingProvider, [], []));
        }

        Assert.Equal(1, await CountSeasonsAsync(workId));
        Assert.Equal(2, await CountEpisodesAsync(workId));
    }

    [Fact]
    public async Task A_snapshot_from_a_second_provider_does_not_renumber_the_structure()
    {
        var workId = await AddSeriesAsync("Fringe", 2008, []);
        await SyncStructureAsync(
            workId,
            _metadata.AddSeries(workId, ClaimingProvider, "Fringe", [Season(1)], [Episode(1, 1), Episode(1, 2)]));

        // A second provider numbers the same show differently; the structure claim keeps it out of the tree.
        await SyncStructureAsync(
            workId,
            _metadata.AddSeries(
                workId, "tvmaze", "Fringe", [Season(1)],
                [Episode(1, 1), Episode(1, 2), Episode(1, 3), Episode(1, 4)]));

        Assert.Equal(2, await CountEpisodesAsync(workId));
        Assert.Equal(ClaimingProvider, (await WorkAsync(workId)).StructureProvider);
    }

    [Theory]
    [InlineData(SeriesStatus.Ended, WorkStatus.Ended)]
    [InlineData(SeriesStatus.Cancelled, WorkStatus.Ended)]
    [InlineData(SeriesStatus.Continuing, WorkStatus.Continuing)]
    [InlineData(SeriesStatus.Upcoming, WorkStatus.Announced)]
    public async Task A_snapshot_narrows_the_series_status(SeriesStatus reported, WorkStatus expected)
    {
        // A series is added as Continuing and only a provider snapshot can say otherwise.
        var workId = await AddSeriesAsync("The Wire (status)", 2002, []);
        Assert.Equal(WorkStatus.Continuing, (await WorkAsync(workId)).Status);

        await RefreshAsync(
            workId,
            _metadata.AddSeries(workId, ClaimingProvider, "The Wire", [Season(1)], [Episode(1, 1)], reported));

        Assert.Equal(expected, (await WorkAsync(workId)).Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(SeriesStatus.Unknown)]
    public async Task A_snapshot_that_supplies_no_usable_status_leaves_it_alone(SeriesStatus? reported)
    {
        // The ACL rule: a field is overwritten only when the snapshot actually supplies a value.
        var workId = await AddSeriesAsync("Twin Peaks", 1990, []);

        await RefreshAsync(
            workId,
            _metadata.AddSeries(workId, ClaimingProvider, "Twin Peaks", [Season(1)], [Episode(1, 1)], reported));

        Assert.Equal(WorkStatus.Continuing, (await WorkAsync(workId)).Status);
    }

    [Fact]
    public async Task A_movie_snapshot_never_narrows_the_work_status()
    {
        var workId = await AddMovieAsync("The Matrix (status)");
        Assert.Equal(WorkStatus.Released, (await WorkAsync(workId)).Status);

        await RefreshAsync(workId, _metadata.AddMovie(workId, "tmdb", "The Matrix"));

        Assert.Equal(WorkStatus.Released, (await WorkAsync(workId)).Status);
    }

    [Fact]
    public async Task A_movie_snapshot_is_a_no_op()
    {
        var workId = await AddMovieAsync("The Matrix");
        var snapshotId = _metadata.AddMovie(workId, "tmdb", "The Matrix");

        await RefreshAsync(workId, snapshotId);

        Assert.Equal(0, await CountSeasonsAsync(workId));
        Assert.Equal(0, await CountEpisodesAsync(workId));
        Assert.Empty(_sink.StructureChanged);
    }

    [Fact]
    public async Task Mark_episode_available_is_idempotent_and_publishes_episode_available_once()
    {
        var workId = await SeededSeriesAsync("Chernobyl", episodeCount: 3);
        var episodeId = await EpisodeIdAsync(workId, 1, 1);
        var assetId = Uuid7.New();

        await MarkEpisodeAvailableAsync(episodeId, assetId, Uuid7.New());
        await MarkEpisodeAvailableAsync(episodeId, assetId, Uuid7.New());
        await DrainAsync();

        Assert.Equal(episodeId, Assert.Single(_sink.EpisodeAvailable).EpisodeId);
        Assert.Equal(1, (await WorkAsync(workId)).AvailableEpisodeCount);
    }

    [Fact]
    public async Task Work_available_fires_only_on_the_first_available_episode()
    {
        var workId = await SeededSeriesAsync("Severance", episodeCount: 3);

        await MarkEpisodeAvailableAsync(await EpisodeIdAsync(workId, 1, 1), Uuid7.New(), Uuid7.New());
        await DrainAsync();
        Assert.Single(_sink.WorkAvailable);

        await MarkEpisodeAvailableAsync(await EpisodeIdAsync(workId, 1, 2), Uuid7.New(), Uuid7.New());
        await DrainAsync();

        // Movie-era consumers see one work-level transition, not one per episode.
        Assert.Single(_sink.WorkAvailable);
        var work = await WorkAsync(workId);
        Assert.True(work.HasAsset);
        Assert.Equal(2, work.AvailableEpisodeCount);
    }

    [Fact]
    public async Task A_season_pack_marks_every_covered_episode_available()
    {
        var workId = await SeededSeriesAsync("Andor", episodeCount: 3);
        var units = await EpisodeIdsAsync(workId);
        var assetId = Uuid7.New();

        await DeliverMediaAvailableAsync(NewMediaAvailable(assetId, workId, units));
        await DrainAsync();

        // One event, N units → N commands with distinct keys → N episodes available.
        Assert.Equal(3, await CountCommandsAsync($"mark-episode-available:{assetId}:"));
        Assert.Equal(3, _sink.EpisodeAvailable.Count);
        Assert.Equal(3, (await WorkAsync(workId)).AvailableEpisodeCount);
        Assert.Single(_sink.WorkAvailable);
    }

    [Fact]
    public async Task A_redelivered_media_available_marks_each_episode_once()
    {
        var workId = await SeededSeriesAsync("Dark", episodeCount: 3);
        var units = await EpisodeIdsAsync(workId);
        var media = NewMediaAvailable(Uuid7.New(), workId, units);

        await DeliverMediaAvailableAsync(media);
        await DrainAsync();
        await DeliverMediaAvailableAsync(media); // at-least-once redelivery
        await DrainAsync();

        Assert.Equal(3, _sink.EpisodeAvailable.Count);
        Assert.Equal(3, (await WorkAsync(workId)).AvailableEpisodeCount);
    }

    [Fact]
    public async Task A_movie_media_available_still_marks_the_work()
    {
        var workId = await AddMovieAsync("Arrival");
        var assetId = Uuid7.New();

        // The movie payload: the only unit IS the work, so the work-level command is what must land.
        await DeliverMediaAvailableAsync(NewMediaAvailable(assetId, workId, [workId]));
        await DrainAsync();

        Assert.Equal(1, await CountCommandsAsync($"mark-work-available:{assetId}"));
        Assert.Equal(0, await CountCommandsAsync("mark-episode-available:"));
        Assert.True((await WorkAsync(workId)).HasAsset);
        Assert.Single(_sink.WorkAvailable);
        Assert.Empty(_sink.EpisodeAvailable);
    }

    // -- fixtures --------------------------------------------------------------------------------

    private static MetadataSeason Season(int number, string? title = null) =>
        new(number, title, Overview: null, EpisodeCount: null, AirDate: null, PosterUrl: null, ExternalId: null);

    private static MetadataEpisode Episode(int seasonNumber, int number, int? absoluteNumber = null) => new(
        seasonNumber,
        number,
        Title: $"S{seasonNumber:00}E{number:00}",
        Overview: null,
        AbsoluteNumber: absoluteNumber,
        AirDate: null,
        AirDateTime: null,
        RuntimeMinutes: 50,
        StillUrl: null,
        ExternalId: null,
        IsSpecial: seasonNumber == 0);

    private static MediaAvailable NewMediaAvailable(Guid assetId, Guid workId, IReadOnlyList<Guid> unitIds) => new(
        assetId,
        workId,
        TargetIds: [Uuid7.New()],
        ImportJobId: Uuid7.New(),
        DownloadTaskId: Uuid7.New(),
        FullPath: $"/data/library/{assetId}.mkv",
        Size: 2_000_000_000,
        MediaInfo: MediaInfo.Empty,
        UnitIds: unitIds);

    // -- helpers ---------------------------------------------------------------------------------

    private async Task<Guid> AddSeriesAsync(string title, int? year, IReadOnlyList<ExternalId> externalIds)
    {
        await using var scope = _provider.CreateAsyncScope();
        var commands = scope.ServiceProvider.GetRequiredService<ICatalogCommands>();
        var result = await commands.AddSeriesAsync(title, year, externalIds);
        Assert.True(result.IsSuccess);
        return result.Value.Value;
    }

    private async Task<Guid> AddMovieAsync(string title)
    {
        await using var scope = _provider.CreateAsyncScope();
        var commands = scope.ServiceProvider.GetRequiredService<ICatalogCommands>();
        return (await commands.AddMovieAsync(title, 2016, [])).Value.Value;
    }

    /// <summary>A series with one season of <paramref name="episodeCount"/> episodes already synced.</summary>
    private async Task<Guid> SeededSeriesAsync(string title, int episodeCount)
    {
        var workId = await AddSeriesAsync(title, 2019, []);
        var episodes = Enumerable.Range(1, episodeCount).Select(n => Episode(1, n)).ToList();
        await SyncStructureAsync(workId, _metadata.AddSeries(workId, ClaimingProvider, title, [Season(1)], episodes));
        return workId;
    }

    /// <summary>Drives the whole reaction chain: MetadataRefreshed → commands → structure sync.</summary>
    [Fact]
    public async Task A_snapshot_carries_its_genres_and_classification_onto_the_work()
    {
        var workId = await AddSeriesAsync("Classified Show", 2016, []);

        await RefreshAsync(workId, _metadata.AddMovie(
            workId, ClaimingProvider, "Classified Show", ["Drama", "Thriller"], "TV-MA"));

        // The whole point of the increment, and the step where this kind of field is usually lost:
        // every layer between the provider and here has to name it, and any one of them forgetting
        // fails silently, because a list that arrives empty looks exactly like a provider with
        // nothing to say.
        var work = await WorkAsync(workId);
        Assert.Equal(["Drama", "Thriller"], work.Genres);
        Assert.Equal("TV-MA", work.ContentRating);
    }

    [Fact]
    public async Task A_snapshot_with_nothing_to_say_does_not_blank_what_is_already_known()
    {
        var workId = await AddSeriesAsync("Kept Show", 2017, []);
        await RefreshAsync(workId, _metadata.AddMovie(
            workId, ClaimingProvider, "Kept Show", ["Comedy"], "PG"));

        // A provider that publishes no genres has not said the work has none, and an installation
        // that later names no classification region has not said the work is unrated. Blanking on
        // either would lose what an earlier refresh knew, every time the schedule came round.
        await RefreshAsync(workId, _metadata.AddMovie(workId, ClaimingProvider, "Kept Show"));

        var work = await WorkAsync(workId);
        Assert.Equal(["Comedy"], work.Genres);
        Assert.Equal("PG", work.ContentRating);
    }

    private async Task RefreshAsync(Guid workId, MetadataSnapshotId snapshotId)
    {
        await using (var scope = _provider.CreateAsyncScope())
        {
            foreach (var handler in scope.ServiceProvider.GetServices<IEventHandler<MetadataRefreshed>>())
            {
                await handler.HandleAsync(new MetadataRefreshed(workId, snapshotId.Value, ClaimingProvider));
            }
        }

        await DrainAsync();
    }

    /// <summary>Runs the structure command handler directly, so a second delivery can be forced.</summary>
    private async Task SyncStructureAsync(Guid workId, MetadataSnapshotId snapshotId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<SyncSeriesStructureCommand>>();
        var result = await handler.HandleAsync(new SyncSeriesStructureCommand(workId, snapshotId.Value));
        Assert.True(result.IsSuccess);
    }

    private async Task MarkEpisodeAvailableAsync(Guid episodeId, Guid assetId, Guid targetId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var commands = scope.ServiceProvider.GetRequiredService<ICatalogCommands>();
        await commands.MarkEpisodeAvailableAsync(episodeId, assetId, targetId);
    }

    private async Task DeliverMediaAvailableAsync(MediaAvailable domainEvent)
    {
        await using var scope = _provider.CreateAsyncScope();
        foreach (var handler in scope.ServiceProvider.GetServices<IEventHandler<MediaAvailable>>())
        {
            await handler.HandleAsync(domainEvent);
        }
    }

    private async Task<Work> WorkAsync(Guid workId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        return await dbContext.Works.AsNoTracking().SingleAsync(w => w.Id == workId);
    }

    private async Task<Episode?> EpisodeAsync(Guid episodeId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        return await dbContext.Episodes.AsNoTracking().FirstOrDefaultAsync(e => e.Id == episodeId);
    }

    private async Task<Guid> EpisodeIdAsync(Guid workId, int seasonNumber, int number)
    {
        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        return await dbContext.Episodes
            .Where(e => e.WorkId == workId && e.SeasonNumber == seasonNumber && e.Number == number)
            .Select(e => e.Id)
            .SingleAsync();
    }

    private async Task<IReadOnlyList<Guid>> EpisodeIdsAsync(Guid workId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        return await dbContext.Episodes
            .Where(e => e.WorkId == workId)
            .OrderBy(e => e.SeasonNumber).ThenBy(e => e.Number)
            .Select(e => e.Id)
            .ToListAsync();
    }

    [Fact]
    public async Task A_provider_that_publishes_no_ordering_gets_one_counted_from_the_structure()
    {
        var workId = await AddSeriesAsync("Counted Show", 2011, []);

        // What every provider but TheTVDB sends: a season tree and no absolute numbers anywhere.
        // Before this, an anime release numbered 003 matched nothing on such a series — the search
        // found it, the evaluation could not identify it, and the title was silently never acquired.
        await SyncStructureAsync(workId, _metadata.AddSeries(
            workId,
            ClaimingProvider,
            "Counted Show",
            [Season(0), Season(1), Season(2)],
            [Episode(0, 1), Episode(1, 1), Episode(1, 2), Episode(2, 1)]));

        // Specials are not counted, which is the universal convention: season zero holds recaps and
        // shorts that no absolute ordering includes, and counting them would shift every real episode.
        Assert.Equal([null, 1, 2, 3], await AbsoluteNumbersAsync(workId));
        Assert.Equal([false, true, true, true], await AbsoluteNumbersAreDerivedAsync(workId));
    }

    [Fact]
    public async Task A_partially_published_ordering_is_left_exactly_as_the_provider_sent_it()
    {
        var workId = await AddSeriesAsync("Partial Show", 2012, []);

        // All or nothing. Filling the gaps would invent numbers beside published ones, and the two
        // orderings would collide on the unique index — a legitimate later sync would then be
        // rejected for a number this code made up.
        await SyncStructureAsync(workId, _metadata.AddSeries(
            workId,
            ClaimingProvider,
            "Partial Show",
            [Season(1)],
            [Episode(1, 1, 7), Episode(1, 2), Episode(1, 3)]));

        Assert.Equal([7, null, null], await AbsoluteNumbersAsync(workId));
        Assert.Equal([false, false, false], await AbsoluteNumbersAreDerivedAsync(workId));
    }

    [Fact]
    public async Task A_counted_ordering_gives_way_to_a_published_one()
    {
        var workId = await AddSeriesAsync("Upgraded Show", 2013, []);
        await SyncStructureAsync(workId, _metadata.AddSeries(
            workId, ClaimingProvider, "Upgraded Show", [Season(1)], [Episode(1, 1), Episode(1, 2)]));
        Assert.Equal([true, true], await AbsoluteNumbersAreDerivedAsync(workId));

        // A snapshot that does publish the ordering replaces what was counted, and says so: a counted
        // number is a guess about air order, and a published one is the answer.
        await SyncStructureAsync(workId, AbsoluteSnapshotAsync(workId, [11, 12]));

        Assert.Equal([11, 12], await AbsoluteNumbersAsync(workId));
        Assert.Equal([false, false], await AbsoluteNumbersAreDerivedAsync(workId));
    }

    [Fact]
    public async Task Counting_the_same_structure_twice_changes_nothing()
    {
        var workId = await AddSeriesAsync("Stable Show", 2014, []);
        var episodes = new[] { Episode(1, 1), Episode(1, 2), Episode(1, 3) };

        await SyncStructureAsync(workId, _metadata.AddSeries(
            workId, ClaimingProvider, "Stable Show", [Season(1)], episodes));
        await SyncStructureAsync(workId, _metadata.AddSeries(
            workId, ClaimingProvider, "Stable Show", [Season(1)], episodes));

        // Idempotent, which matters because a metadata refresh runs on a schedule: a counted ordering
        // that churned would rewrite three rows on every pass for ever.
        Assert.Equal([1, 2, 3], await AbsoluteNumbersAsync(workId));
    }

    [Fact]
    public async Task An_absolute_release_resolves_on_a_series_no_provider_numbered()
    {
        var workId = await AddSeriesAsync("Resolvable Show", 2015, []);
        await SyncStructureAsync(workId, _metadata.AddSeries(
            workId,
            ClaimingProvider,
            "Resolvable Show",
            [Season(1), Season(2)],
            [Episode(1, 1), Episode(1, 2), Episode(1, 3), Episode(2, 1)]));

        // The whole point, through the interface Decision and Import actually call: a release named
        // "Resolvable Show - 004" now finds S02E01 on a series whose provider published no ordering.
        await using var scope = _provider.CreateAsyncScope();
        var query = scope.ServiceProvider.GetRequiredService<ICatalogSeriesQuery>();
        var resolved = Assert.Single(await query.ResolveByAbsoluteNumberAsync(new WorkId(workId), 4));

        Assert.Equal(2, resolved.SeasonNumber);
        Assert.Equal(1, resolved.Number);
    }

    private async Task<IReadOnlyList<bool>> AbsoluteNumbersAreDerivedAsync(Guid workId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        return await dbContext.Episodes
            .Where(e => e.WorkId == workId)
            .OrderBy(e => e.SeasonNumber).ThenBy(e => e.Number)
            .Select(e => e.AbsoluteNumberIsDerived)
            .ToListAsync();
    }

    /// <summary>One season whose episodes carry <paramref name="absoluteNumbers"/>, in episode order.</summary>
    private MetadataSnapshotId AbsoluteSnapshotAsync(Guid workId, IReadOnlyList<int> absoluteNumbers) =>
        _metadata.AddSeries(
            workId,
            ClaimingProvider,
            "Absolute Show",
            [Season(1)],
            [.. absoluteNumbers.Select((absolute, index) => Episode(1, index + 1, absolute))]);

    private async Task<IReadOnlyList<int?>> AbsoluteNumbersAsync(Guid workId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        return await dbContext.Episodes
            .Where(e => e.WorkId == workId)
            .OrderBy(e => e.SeasonNumber).ThenBy(e => e.Number)
            .Select(e => e.AbsoluteNumber)
            .ToListAsync();
    }

    private async Task<int> CountSeasonsAsync(Guid workId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        return await dbContext.Seasons.CountAsync(s => s.WorkId == workId);
    }

    private async Task<int> CountEpisodesAsync(Guid workId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        return await dbContext.Episodes.CountAsync(e => e.WorkId == workId);
    }

    private async Task<int> CountCommandsAsync(string keyPrefix)
    {
        await using var scope = _provider.CreateAsyncScope();
        var operationsDb = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
        return await operationsDb.Commands.CountAsync(c => c.IdempotencyKey.StartsWith(keyPrefix));
    }

    private Task DrainAsync() => MessageDriver.DrainAsync(_provider);
}
