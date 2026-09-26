using Cinomni.Metadata.Contracts;
using Cinomni.Metadata.Persistence;
using Cinomni.Metadata.Providers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Cinomni.Metadata.Tests;

/// <summary>
/// Integration tests for the series structure of a metadata snapshot against a real PostgreSQL instance:
/// seasons and episodes persist as cascade children, the tree reads back ordered by season then episode,
/// the natural keys are enforced by unique indexes, and — the load-bearing one — the movie snapshot path
/// never joins the episode table.
/// <para>
/// The database name is deliberately distinct from every other suite: each test host opens with
/// <c>EnsureDeletedAsync</c> on a hard-coded name and <c>dotnet test</c> runs projects in parallel, so a
/// shared name drops another suite's database mid-run.
/// </para>
/// </summary>
public sealed class MetadataSeriesStructureTests : IAsyncLifetime
{
    private const string Database = "cinomni_test_metadata_series";
    private const string EpisodesTable = "metadata_episodes";
    private const string SeasonsTable = "metadata_seasons";
    private const string ArtworkTable = "metadata_artwork";

    private ServiceProvider _host = null!;
    private SqlCapture _sql = null!;

    public async Task InitializeAsync()
    {
        SqlCapture? capture = null;
        _host = await MetadataTestHost.CreateAsync(
            Database,
            sources: [],
            configureOptions: null,
            configureServices: services => capture = SqlCapture.Register(services));
        _sql = capture!;
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task A_series_snapshot_persists_its_seasons_and_episodes()
    {
        var snapshotId = await SeedSeriesAsync(
            seasons: [Season(1), Season(2)],
            episodes: [Episode(1, 1), Episode(1, 2), Episode(2, 1)]);

        await using var scope = _host.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MetadataDbContext>();

        Assert.Equal(2, await dbContext.Seasons.CountAsync(s => s.SnapshotId == snapshotId));
        Assert.Equal(3, await dbContext.Episodes.CountAsync(e => e.SnapshotId == snapshotId));

        var stored = await dbContext.Episodes
            .AsNoTracking()
            .SingleAsync(e => e.SnapshotId == snapshotId && e.SeasonNumber == 1 && e.Number == 2);
        Assert.Equal("S01E02", stored.Title);
        Assert.Equal(new DateOnly(2026, 1, 2), stored.AirDate);
        Assert.Equal(12, stored.AbsoluteNumber);
    }

    [Fact]
    public async Task GetSeriesStructure_returns_the_tree_ordered_by_season_then_episode()
    {
        // Inserted deliberately out of order — including the specials bucket (season 0).
        var snapshotId = await SeedSeriesAsync(
            seasons: [Season(2), Season(0), Season(1)],
            episodes: [Episode(2, 2), Episode(1, 2), Episode(0, 1), Episode(2, 1), Episode(1, 1)]);

        var structure = await GetStructureAsync(snapshotId);

        Assert.NotNull(structure);
        Assert.Equal([0, 1, 2], structure.Seasons.Select(s => s.Number));
        Assert.Equal(
            [(0, 1), (1, 1), (1, 2), (2, 1), (2, 2)],
            structure.Episodes.Select(e => (e.SeasonNumber, e.Number)));
    }

    [Fact]
    public async Task GetSnapshot_does_not_load_episodes()
    {
        var snapshotId = await SeedSeriesAsync(
            seasons: [Season(1)],
            episodes: [Episode(1, 1), Episode(1, 2), Episode(1, 3)],
            artwork: [Poster("https://image.example/series-poster.jpg"), Backdrop()]);

        _sql.Clear();
        var snapshot = await GetSnapshotAsync(snapshotId);

        // The detail path reads the snapshot plus its artwork and nothing else: joining seasons and
        // episodes onto that include is an artwork x episodes cartesian product on every movie page.
        Assert.NotNull(snapshot);
        Assert.Equal(2, snapshot.Artwork.Count);
        Assert.True(_sql.Touched(ArtworkTable), "the snapshot read is expected to load its artwork");
        Assert.False(_sql.Touched(EpisodesTable), Because(EpisodesTable));
        Assert.False(_sql.Touched(SeasonsTable), Because(SeasonsTable));

        // Positive control: the same capture does see the episode table when the structure is asked for,
        // so the assertions above cannot pass merely because nothing was captured.
        _sql.Clear();
        await GetStructureAsync(snapshotId);
        Assert.True(_sql.Touched(EpisodesTable), "the structure query is expected to read the episodes");
    }

    [Fact]
    public async Task Duplicate_season_numbers_in_one_snapshot_are_rejected_by_the_unique_index()
    {
        var snapshotId = await SeedSeriesAsync(seasons: [Season(1)], episodes: []);

        await using var scope = _host.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MetadataDbContext>();
        dbContext.Seasons.Add(MetadataSeasonRecord.Create(snapshotId, Season(1)));

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => dbContext.SaveChangesAsync());
        Assert.Equal(
            PostgresErrorCodes.UniqueViolation,
            Assert.IsType<PostgresException>(exception.InnerException).SqlState);
    }

    [Fact]
    public async Task Duplicate_episode_numbers_in_one_season_are_rejected_by_the_unique_index()
    {
        var snapshotId = await SeedSeriesAsync(seasons: [Season(1)], episodes: [Episode(1, 1)]);

        await using var scope = _host.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MetadataDbContext>();
        dbContext.Episodes.Add(MetadataEpisodeRecord.Create(snapshotId, Episode(1, 1)));

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => dbContext.SaveChangesAsync());
        Assert.Equal(
            PostgresErrorCodes.UniqueViolation,
            Assert.IsType<PostgresException>(exception.InnerException).SqlState);
    }

    [Fact]
    public async Task The_same_episode_number_in_two_seasons_is_allowed()
    {
        var snapshotId = await SeedSeriesAsync(
            seasons: [Season(1), Season(2)],
            episodes: [Episode(1, 5), Episode(2, 5)]);

        var structure = await GetStructureAsync(snapshotId);

        Assert.NotNull(structure);
        Assert.Equal(2, structure.Episodes.Count(e => e.Number == 5));
    }

    [Fact]
    public async Task An_episode_keeps_both_the_published_date_and_the_tz_aware_instant()
    {
        // The provider publishes 2026-07-28 as the air date and 21:00-04:00 as the tz-aware instant —
        // the same broadcast, one day apart once normalised to UTC. Both must survive independently:
        // date-based release matching compares the former, the unaired gate evaluates the latter.
        var airDate = new DateOnly(2026, 7, 28);
        var airStamp = new DateTimeOffset(2026, 7, 28, 21, 0, 0, TimeSpan.FromHours(-4));
        var snapshotId = await SeedSeriesAsync(
            seasons: [Season(1)],
            episodes: [Episode(1, 1) with { AirDate = airDate, AirDateTime = airStamp }]);

        var structure = await GetStructureAsync(snapshotId);

        var episode = Assert.Single(structure!.Episodes);
        Assert.Equal(airDate, episode.AirDate);
        Assert.Equal(airStamp, episode.AirDateTime);
        // Stored as UTC, so the instant round-trips with a zero offset and a different calendar day.
        Assert.Equal(TimeSpan.Zero, episode.AirDateTime!.Value.Offset);
        Assert.Equal(new DateOnly(2026, 7, 29), DateOnly.FromDateTime(episode.AirDateTime.Value.UtcDateTime));
    }

    [Fact]
    public async Task A_movie_snapshot_returns_an_empty_structure()
    {
        var snapshotId = await SeedSeriesAsync(seasons: [], episodes: [], kind: MetadataMediaKind.Movie);

        var structure = await GetStructureAsync(snapshotId);

        Assert.NotNull(structure);
        Assert.Empty(structure.Seasons);
        Assert.Empty(structure.Episodes);
    }

    [Fact]
    public async Task The_structure_of_an_unknown_snapshot_is_null() =>
        Assert.Null(await GetStructureAsync(Guid.NewGuid()));

    [Fact]
    public async Task Deleting_a_snapshot_cascades_its_seasons_and_episodes()
    {
        var snapshotId = await SeedSeriesAsync(seasons: [Season(1)], episodes: [Episode(1, 1), Episode(1, 2)]);

        await using (var scope = _host.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<MetadataDbContext>();
            await dbContext.Snapshots.Where(s => s.Id == snapshotId).ExecuteDeleteAsync();
        }

        await using var verify = _host.CreateAsyncScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<MetadataDbContext>();
        Assert.Equal(0, await verifyDb.Seasons.CountAsync(s => s.SnapshotId == snapshotId));
        Assert.Equal(0, await verifyDb.Episodes.CountAsync(e => e.SnapshotId == snapshotId));
    }

    [Fact]
    public async Task Getting_the_latest_snapshot_for_a_work_returns_the_most_recently_fetched()
    {
        var workId = Guid.NewGuid();
        var older = DateTimeOffset.UtcNow.AddDays(-3);
        await SeedSeriesAsync(seasons: [], episodes: [], workId: workId, fetchedAt: older, title: "Stale");
        await SeedSeriesAsync(seasons: [], episodes: [], workId: workId, fetchedAt: older.AddDays(2), title: "Fresh");

        await using var scope = _host.CreateAsyncScope();
        var latest = await scope.ServiceProvider.GetRequiredService<IMetadataQuery>()
            .GetLatestSnapshotForWorkAsync(workId);

        Assert.NotNull(latest);
        Assert.Equal("Fresh", latest.Title);
    }

    [Fact]
    public async Task A_series_snapshot_carries_its_status_and_external_ids()
    {
        var snapshotId = await SeedSeriesAsync(
            seasons: [Season(1)],
            episodes: [],
            series: new SeriesSnapshotDetails(
                SeriesStatus.Continuing,
                new DateOnly(2002, 6, 2),
                new DateOnly(2008, 3, 9),
                TvdbId: "79126",
                ImdbId: "tt0306414",
                TmdbId: "1438",
                SeasonOrder: "official"));

        var snapshot = await GetSnapshotAsync(snapshotId);

        Assert.NotNull(snapshot);
        Assert.Equal(SeriesStatus.Continuing, snapshot.SeriesStatus);
        Assert.Equal(new DateOnly(2002, 6, 2), snapshot.FirstAired);
        Assert.Equal(new DateOnly(2008, 3, 9), snapshot.LastAired);
        Assert.Equal("79126", snapshot.TvdbId);
        Assert.Equal("tt0306414", snapshot.ImdbId);
        Assert.Equal("1438", snapshot.TmdbId);
        Assert.Equal("official", snapshot.SeasonOrder);
    }

    [Fact]
    public async Task A_movie_snapshot_carries_no_series_fields()
    {
        // The movie regression: nothing about the widened contract leaks into the movie path.
        var snapshotId = await SeedSeriesAsync(seasons: [], episodes: [], kind: MetadataMediaKind.Movie);

        var snapshot = await GetSnapshotAsync(snapshotId);

        Assert.NotNull(snapshot);
        Assert.Null(snapshot.SeriesStatus);
        Assert.Null(snapshot.FirstAired);
        Assert.Null(snapshot.LastAired);
        Assert.Null(snapshot.TvdbId);
        Assert.Null(snapshot.SeasonOrder);
    }

    // -- helpers ---------------------------------------------------------------------------------

    private static string Because(string table) =>
        $"GetSnapshotAsync must not read {table} — see the cartesian-product note on MetadataQuery.";

    private static MetadataSeason Season(int number) =>
        new(number, $"Season {number}", $"Overview of season {number}", EpisodeCount: null, AirDate: null, PosterUrl: null, ExternalId: null);

    private static MetadataEpisode Episode(int season, int number) => new(
        season,
        number,
        $"S{season:00}E{number:00}",
        Overview: null,
        AbsoluteNumber: (season * 10) + number,
        AirDate: new DateOnly(2026, Math.Max(season, 1), number),
        AirDateTime: null,
        RuntimeMinutes: 45,
        StillUrl: null,
        ExternalId: null,
        IsSpecial: season == 0);

    private static ProviderArtwork Poster(string url) =>
        new(ArtworkKind.Poster, url, "en", 2000, 3000, VoteAverage: 8.0, VoteCount: 10);

    private static ProviderArtwork Backdrop() =>
        new(ArtworkKind.Backdrop, "https://image.example/series-backdrop.jpg", null, 3840, 2160, VoteAverage: 7.0, VoteCount: 5);

    private async Task<Guid> SeedSeriesAsync(
        IReadOnlyList<MetadataSeason> seasons,
        IReadOnlyList<MetadataEpisode> episodes,
        IReadOnlyList<ProviderArtwork>? artwork = null,
        MetadataMediaKind kind = MetadataMediaKind.Series,
        SeriesSnapshotDetails? series = null,
        Guid? workId = null,
        DateTimeOffset? fetchedAt = null,
        string title = "The Wire")
    {
        await using var scope = _host.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MetadataDbContext>();

        var snapshot = MetadataSnapshotRecord.Create(
            workId ?? Guid.NewGuid(),
            provider: "tvdb",
            kind,
            externalId: "79126",
            title,
            originalTitle: null,
            year: 2002,
            overview: null,
            runtimeMinutes: 60,
            originalLanguage: "en",
            posterUrl: null,
            backdropUrl: null,
            rawResponse: "{}",
            fetchedAt ?? DateTimeOffset.UtcNow,
            series);

        foreach (var season in seasons)
        {
            snapshot.Seasons.Add(MetadataSeasonRecord.Create(snapshot.Id, season));
        }

        foreach (var episode in episodes)
        {
            snapshot.Episodes.Add(MetadataEpisodeRecord.Create(snapshot.Id, episode));
        }

        for (var i = 0; i < (artwork?.Count ?? 0); i++)
        {
            snapshot.Artwork.Add(MetadataArtworkRecord.Create(snapshot.Id, artwork![i], isSelected: i == 0, ordinal: i));
        }

        dbContext.Snapshots.Add(snapshot);
        await dbContext.SaveChangesAsync();
        return snapshot.Id;
    }

    private async Task<MetadataSeriesStructure?> GetStructureAsync(Guid snapshotId)
    {
        await using var scope = _host.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IMetadataQuery>()
            .GetSeriesStructureAsync(new MetadataSnapshotId(snapshotId));
    }

    private async Task<MetadataSnapshot?> GetSnapshotAsync(Guid snapshotId)
    {
        await using var scope = _host.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IMetadataQuery>()
            .GetSnapshotAsync(new MetadataSnapshotId(snapshotId));
    }
}
