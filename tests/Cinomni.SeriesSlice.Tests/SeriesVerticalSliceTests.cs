using Cinomni.Acquisition.Contracts;
using Cinomni.Catalog.Contracts;
using Cinomni.Decision.Contracts;
using Cinomni.Import.Files;
using Cinomni.Monitoring.Contracts;
using Cinomni.Monitoring.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.SeriesSlice.Tests;

/// <summary>
/// The acceptance suite of the series slice: every fact here crosses at least four module boundaries,
/// and the headline one crosses nine. Waves 1-3 built each module against the contracts in isolation,
/// so this is the first place the seams between them are exercised at all.
/// <para>
/// One class, one database. xUnit gives each fact its own instance (so each starts from a freshly
/// migrated schema) but runs the facts of a class serially, which is what makes a single hard-coded
/// database name safe — the suites parallelise across projects, not within a class.
/// </para>
/// </summary>
public sealed class SeriesVerticalSliceTests : IAsyncLifetime
{
    private const string Database = "cinomni_test_series_slice";

    /// <summary>Well above the 50 MiB floor the series profile applies to every allowed quality.</summary>
    private const long PackBytes = 8L * 1024 * 1024 * 1024;

    private const long EpisodeBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>Bytes on "disk" — only the import floor (100 in this host) applies here.</summary>
    private const long FileBytes = 4096;

    private readonly FakeSeriesMetadataSource _metadata = new();
    private readonly FakeIndexerCatalog _indexers = new();
    private readonly FakeTorrentEngine _engine = new();
    private readonly FakeSliceFileSystem _fileSystem = new();
    private readonly FakeSliceMediaProbe _probe = new();

    private ServiceProvider _host = null!;
    private SeriesSliceDriver _driver = null!;

    public async Task InitializeAsync()
    {
        _host = await SeriesTestHost.CreateAsync(Database, _metadata, _indexers, _engine, _fileSystem, _probe);
        _driver = new SeriesSliceDriver(_host);
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Adding_a_series_and_refreshing_metadata_materialises_the_hierarchy()
    {
        _metadata.ScriptSeries(FakeSeriesMetadataSource.SeasonOf(1, episodes: 4));

        var workId = await AddAndRefreshSeriesAsync();

        // Catalog holds the tree the provider published...
        var seasons = await _driver.SeasonsAsync(workId);
        var episodes = await _driver.EpisodesAsync(workId);
        Assert.Equal(1, Assert.Single(seasons).Number);
        Assert.Equal(4, episodes.Count);
        var work = await _driver.WorkAsync(workId);
        Assert.Equal(4, work.EpisodeCount);
        Assert.Equal(FakeSeriesMetadataSource.ProviderName, work.StructureProvider);

        // ...and Monitoring mirrors it, joined back to the catalog by unit id, not by position.
        var targets = await _driver.TargetsAsync(workId);
        var root = Assert.Single(targets, t => t.Kind == TargetKind.Series);
        Assert.Equal(workId.Value, root.TargetRef);
        var season = Assert.Single(targets, t => t.Kind == TargetKind.Season);
        Assert.Equal(seasons[0].Id, season.TargetRef);
        Assert.Equal(root.Id, season.ParentTargetId);

        var episodeTargets = targets.Where(t => t.Kind == TargetKind.Episode).ToList();
        Assert.Equal(4, episodeTargets.Count);
        Assert.Equal(
            episodes.Select(e => e.Id).Order(),
            episodeTargets.Select(t => t.TargetRef).Order());
        Assert.All(episodeTargets, t => Assert.Equal(season.Id, t.ParentTargetId));

        // The policy root is not acquirable, so it opens no goal; every leaf does.
        var intents = await _driver.IntentsAsync();
        Assert.DoesNotContain(intents, i => i.TargetId == root.Id);
        Assert.Equal(5, intents.Count); // one season goal plus four episode goals
    }

    [Fact]
    public async Task Applying_first_season_monitors_only_season_one_and_opens_its_goals()
    {
        _metadata.ScriptSeries(
            FakeSeriesMetadataSource.SeasonOf(1, episodes: 3),
            FakeSeriesMetadataSource.SeasonOf(2, episodes: 3));

        // The policy is set on the root before any structure exists — which is the real ordering, since
        // WorkAdded fires long before the first metadata refresh. The structure sync then cascades the
        // mode it finds stored, rather than re-deciding it.
        var workId = await _driver.AddSeriesAsync();
        await _driver.DrainAsync();
        await _driver.ApplyPolicyAsync(workId, MonitoringMode.FirstSeason);
        await _driver.RefreshSeriesAsync(workId);

        var targets = await _driver.TargetsAsync(workId);
        var firstSeason = Episodes(targets, seasonNumber: 1);
        var secondSeason = Episodes(targets, seasonNumber: 2);
        Assert.Equal(3, firstSeason.Count);
        Assert.Equal(3, secondSeason.Count);
        Assert.All(firstSeason, t => Assert.True(t.Monitored));
        Assert.All(secondSeason, t => Assert.False(t.Monitored));

        // A goal is opened for what is watched and for nothing else: an unmonitored season must not
        // cost an acquisition intent, or narrowing a 10-season show would still open 200 goals.
        var intents = await _driver.IntentsAsync();
        var watched = firstSeason.Select(t => t.TargetRef).ToHashSet();
        Assert.Equal(3, intents.Count(i => i.UnitId is { } unit && watched.Contains(unit)));
        var unwatched = secondSeason.Select(t => t.TargetRef).ToHashSet();
        Assert.DoesNotContain(intents, i => i.UnitId is { } unit && unwatched.Contains(unit));
    }

    [Fact]
    public async Task A_season_pack_is_searched_once_selected_once_downloaded_once_and_satisfies_every_episode()
    {
        const string packGuid = "pack-s01";
        const string packName = "The.Frontier.S01.1080p.WEB-DL.x264-GRP";

        _metadata.ScriptSeries(FakeSeriesMetadataSource.SeasonOf(1, episodes: 4));
        var workId = await AddAndRefreshSeriesAsync();
        var episodes = await _driver.EpisodesAsync(workId);

        await _driver.AddTvIndexerAsync();
        _indexers.Set(
            SeriesSliceDriver.IndexerName,
            FakeIndexerCatalog.Release(packGuid, packName, PackBytes, SeriesSliceDriver.IndexerName));
        _engine.ScriptRelease(UrlOf(packGuid), packName);
        SeedStagedFiles(packName, "The.Frontier.S01E01", "The.Frontier.S01E02", "The.Frontier.S01E03", "The.Frontier.S01E04");

        await _driver.SweepAsync();
        await _driver.CompleteDownloadAsync(_engine, UrlOf(packGuid));

        // ONE search: four episode-level requests for the same pack would have been four fan-outs to
        // every configured indexer, which is what gets an account banned.
        var criterion = Assert.Single(_indexers.Queries);
        Assert.Equal(TargetKind.Season.ToString(), criterion.ContentKind);
        Assert.Equal(1, criterion.SeasonNumber);
        Assert.Null(criterion.EpisodeNumber);

        // ONE download, carrying every unit it serves.
        var task = Assert.Single(await _driver.DownloadTasksAsync());
        Assert.Equal(episodes.Select(e => e.Id).Order(), task.Units.Select(u => u.UnitId).Order());
        Assert.Single(_engine.AddedInfoHashes);

        // ONE import job, four landed files, four assets, four asset↔unit links.
        var job = Assert.Single(await _driver.ImportJobsAsync());
        Assert.Equal(4, job.Matches.Count);
        Assert.Equal(4, _fileSystem.Linked.Count);
        var assets = await _driver.AssetsAsync();
        Assert.Equal(4, assets.Count);
        Assert.Equal(4, assets.Select(a => a.Versions.Single().FullPath).Distinct().Count());
        var links = await _driver.UnitLinksAsync();
        Assert.Equal(4, links.Count);
        Assert.Equal(episodes.Select(e => e.Id).Order(), links.Select(l => l.UnitId).Order());

        // Every episode is available in the catalog and no longer missing in Monitoring...
        Assert.Equal(4, (await _driver.WorkAsync(workId)).AvailableEpisodeCount);
        var targets = await _driver.TargetsAsync(workId);
        Assert.All(Episodes(targets, seasonNumber: 1), t => Assert.False(t.IsMissing));

        // ...and every goal the one pack met is closed, including the four that never opened an attempt.
        var intents = await _driver.IntentsAsync();
        Assert.Equal(5, intents.Count);
        Assert.All(intents, i => Assert.Equal(IntentState.Available, i.State));
    }

    [Fact]
    public async Task A_single_episode_release_satisfies_exactly_one_episode()
    {
        const string episodeGuid = "single-s01e01";
        const string releaseName = "The.Frontier.S01E01.1080p.WEB-DL.x264-GRP";

        // One episode in the season is below the pack floor (3 missing), so the sweep asks per episode.
        _metadata.ScriptSeries(FakeSeriesMetadataSource.SeasonOf(1, episodes: 1));
        var workId = await AddAndRefreshSeriesAsync();
        var episode = Assert.Single(await _driver.EpisodesAsync(workId));

        await _driver.AddTvIndexerAsync();
        _indexers.Set(
            SeriesSliceDriver.IndexerName,
            FakeIndexerCatalog.Release(episodeGuid, releaseName, EpisodeBytes, SeriesSliceDriver.IndexerName));
        _engine.ScriptRelease(UrlOf(episodeGuid), releaseName);
        SeedStagedFiles(releaseName, "The.Frontier.S01E01");

        await _driver.SweepAsync();
        await _driver.CompleteDownloadAsync(_engine, UrlOf(episodeGuid));

        var criterion = Assert.Single(_indexers.Queries);
        Assert.Equal(TargetKind.Episode.ToString(), criterion.ContentKind);
        Assert.Equal(1, criterion.EpisodeNumber);

        var asset = Assert.Single(await _driver.AssetsAsync());
        Assert.Equal(episode.Id, Assert.Single(asset.UnitLinks).UnitId);
        Assert.Equal(1, (await _driver.WorkAsync(workId)).AvailableEpisodeCount);

        // Both the episode and the season it belongs to stop being missing — the season through the
        // rollup, which is what stops the next sweep asking for content already on disk.
        var targets = await _driver.TargetsAsync(workId);
        Assert.False(Assert.Single(Episodes(targets, seasonNumber: 1)).IsMissing);
        Assert.False(Assert.Single(targets, t => t.Kind == TargetKind.Season).IsMissing);

        // Exactly one goal is met: the episode's. The season goal never opened an attempt and its own
        // unit did not land, so it rests in Searching — the same resting state the unaired gate
        // gives an unaired episode. Nothing re-searches it, because its target is no longer missing.
        var intents = await _driver.IntentsAsync();
        Assert.Equal(IntentState.Available, Assert.Single(intents, i => i.UnitId == episode.Id).State);
        Assert.Equal(IntentState.Searching, Assert.Single(intents, i => i.UnitId != episode.Id).State);
    }

    [Fact]
    public async Task A_new_season_from_a_later_metadata_refresh_is_monitored()
    {
        _metadata.ScriptSeries(FakeSeriesMetadataSource.SeasonOf(1, episodes: 3));
        var workId = await AddAndRefreshSeriesAsync();
        Assert.Equal(3, (await _driver.EpisodesAsync(workId)).Count);

        // The show carries on. This is the regression: the season targets are materialised under a key
        // that changes with the snapshot, because apply-policy:{workId} was consumed forever by
        // WorkAdded and would have swallowed every later season with no error and no log line.
        _metadata.ScriptSeries(
            FakeSeriesMetadataSource.SeasonOf(1, episodes: 3),
            FakeSeriesMetadataSource.SeasonOf(2, episodes: 2));
        await _driver.RefreshSeriesAsync(workId);

        Assert.Equal(5, (await _driver.EpisodesAsync(workId)).Count);
        Assert.Equal(2, (await _driver.SeasonsAsync(workId)).Count);

        var targets = await _driver.TargetsAsync(workId);
        Assert.Equal(2, targets.Count(t => t.Kind == TargetKind.Season));
        var newSeason = Episodes(targets, seasonNumber: 2);
        Assert.Equal(2, newSeason.Count);
        Assert.All(newSeason, t => Assert.True(t.Monitored));

        // ...and each new episode opens its own goal, so the next sweep actually searches for it.
        var intents = await _driver.IntentsAsync();
        var newUnits = newSeason.Select(t => t.TargetRef).ToHashSet();
        Assert.Equal(2, intents.Count(i => i.UnitId is { } unit && newUnits.Contains(unit)));
    }

    [Fact]
    public async Task A_wrong_episode_release_is_rejected_and_never_downloaded()
    {
        const string wrongGuid = "wrong-s01e07";

        _metadata.ScriptSeries(FakeSeriesMetadataSource.SeasonOf(1, episodes: 1));
        var workId = await AddAndRefreshSeriesAsync();

        await _driver.AddTvIndexerAsync();
        _indexers.Set(
            SeriesSliceDriver.IndexerName,
            FakeIndexerCatalog.Release(
                wrongGuid, "The.Frontier.S01E07.1080p.WEB-DL.x264-GRP", EpisodeBytes, SeriesSliceDriver.IndexerName));

        await _driver.SweepAsync();

        // The search really ran and really saw the release — without this the rest of the fact would
        // hold just as well if the sweep had never asked anything at all.
        Assert.Equal(1, Assert.Single(_indexers.Queries).EpisodeNumber);
        var evaluation = Assert.Single(await _driver.EvaluationsAsync());
        Assert.Equal(Verdict.RejectedPermanent, evaluation.Verdict);
        Assert.Contains(
            evaluation.Reasons,
            r => r.Rule == "MatchesRequestedEpisode" && r.Outcome == ReasonOutcome.Fail);

        // Decision is the only identity check in the pipeline: the indexer query is a request, not a
        // guarantee, and before it existed a search for S01E01 that returned S01E07 was downloaded.
        Assert.Empty(await _driver.DownloadTasksAsync());
        Assert.Empty(_engine.AddedInfoHashes);
        Assert.Empty(await _driver.ImportJobsAsync());
        Assert.Empty(await _driver.AssetsAsync());

        var targets = await _driver.TargetsAsync(workId);
        Assert.True(Assert.Single(Episodes(targets, seasonNumber: 1)).IsMissing);

        // The goal survives, still searchable, without having burned itself on the wrong file.
        Assert.All(await _driver.IntentsAsync(), i => Assert.NotEqual(IntentState.Available, i.State));
    }

    [Fact]
    public async Task Adding_a_movie_through_the_same_host_behaves_exactly_as_before()
    {
        const string movieGuid = "movie-1";
        const string releaseName = "The.Matrix.1999.1080p.BluRay.x264-GRP";

        var workId = await _driver.AddMovieAsync(FakeSeriesMetadataSource.MovieTitle, FakeSeriesMetadataSource.MovieYear);
        await _driver.DrainAsync();

        await _driver.AddTvIndexerAsync();
        _indexers.Set(
            SeriesSliceDriver.IndexerName,
            FakeIndexerCatalog.Release(movieGuid, releaseName, PackBytes, SeriesSliceDriver.IndexerName));
        _engine.ScriptRelease(UrlOf(movieGuid), releaseName);
        _fileSystem.SeedContent(
            StagedPath(releaseName),
            new ImportFileEntry($"{StagedPath(releaseName)}/{releaseName}.mkv", FileBytes));

        await _driver.SweepAsync();
        await _driver.CompleteDownloadAsync(_engine, UrlOf(movieGuid));

        // Exactly one target, and it is the movie row — no hierarchy leaks into the flat path.
        var target = Assert.Single(await _driver.TargetsAsync(workId));
        Assert.Equal(TargetKind.Movie, target.Kind);
        Assert.Equal(workId.Value, target.TargetRef);
        Assert.False(target.IsMissing);
        Assert.Empty(await _driver.SeasonsAsync(workId));
        Assert.Empty(await _driver.EpisodesAsync(workId));

        // The criterion is the movie criterion it always was: no season, no episode, no air date.
        var criterion = Assert.Single(_indexers.Queries);
        Assert.Equal(TargetKind.Movie.ToString(), criterion.ContentKind);
        Assert.Equal(FakeSeriesMetadataSource.MovieTitle, criterion.Term);
        Assert.Null(criterion.SeasonNumber);
        Assert.Null(criterion.EpisodeNumber);
        Assert.Null(criterion.AirDate);

        // One file, one asset, one unit — and the unit is the work, which is the whole movie identity.
        Assert.Single(_fileSystem.Linked);
        var asset = Assert.Single(await _driver.AssetsAsync());
        Assert.Equal(workId.Value, asset.WorkId);
        Assert.Equal(workId.Value, Assert.Single(asset.UnitLinks).UnitId);
        Assert.Equal(target.Id, Assert.Single(asset.TargetLinks).TargetId);
        Assert.True((await _driver.WorkAsync(workId)).HasAsset);

        Assert.Equal(IntentState.Available, Assert.Single(await _driver.IntentsAsync()).State);
    }

    // -- fixtures --------------------------------------------------------------------------------

    private static string UrlOf(string releaseGuid) => $"magnet:?xt=urn:btih:{releaseGuid}";

    /// <summary>Where the engine staged a torrent: the save path plus the torrent's own name.</summary>
    private static string StagedPath(string torrentName) => $"{SeriesTestHost.StagingPath}/{torrentName}";

    private static List<MonitoredTarget> Episodes(IEnumerable<MonitoredTarget> targets, int seasonNumber) =>
        targets
            .Where(t => t.Kind == TargetKind.Episode && t.SeasonNumber == seasonNumber)
            .OrderBy(t => t.EpisodeNumber)
            .ToList();

    /// <summary>Adds the scripted series, applies the default policy and materialises its structure.</summary>
    private async Task<WorkId> AddAndRefreshSeriesAsync()
    {
        var workId = await _driver.AddSeriesAsync();
        await _driver.DrainAsync();
        await _driver.RefreshSeriesAsync(workId);
        return workId;
    }

    /// <summary>Puts the episode files of a completed torrent where Import will look for them.</summary>
    private void SeedStagedFiles(string torrentName, params string[] fileStems)
    {
        var contentPath = StagedPath(torrentName);
        _fileSystem.SeedContent(
            contentPath,
            [.. fileStems.Select(stem => new ImportFileEntry($"{contentPath}/{stem}.1080p.WEB-DL.x264-GRP.mkv", FileBytes))]);
    }
}
