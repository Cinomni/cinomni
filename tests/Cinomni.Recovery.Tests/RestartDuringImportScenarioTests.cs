using Cinomni.Acquisition.Contracts;
using Cinomni.Import.Contracts;
using Cinomni.Import.Files;
using Cinomni.Import.Messaging;
using Cinomni.Import.Persistence;
using Cinomni.Kernel.Identifiers;
using Cinomni.Recovery.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Recovery.Tests;

/// <summary>
/// Validation scenario 15 — the process restarts while an import is running.
/// <para>
/// A season pack is the case worth testing, because it is the only one where an import is long
/// enough to be interrupted halfway and where "half of it landed" is a state the product can
/// actually be in. Two interruptions are covered, and they fail differently: a batch that lost one
/// file to a verify failure (the job commits and returns to <c>Pending</c>), and a process that
/// disappeared between two hardlinks (nothing commits at all, and files are already on disk).
/// </para>
/// <para>
/// Both are unrecoverable without the mechanism this milestone added. Import reacts to
/// <c>DownloadCompleted</c> under <c>process-completed-download:{downloadTaskId}</c>, and the command
/// queue drops a repeat of a spent key — so before startup recovery existed, a job that reached
/// <c>Pending</c> could never be driven again and the files it had not landed never arrived.
/// </para>
/// </summary>
[Trait("Scenario", "15")]
public sealed class RestartDuringImportScenarioTests : IAsyncLifetime
{
    private const string Database = "cinomni_test_recovery_import";
    private const string ReleaseGuid = "release-restart-import";
    private const string DownloadUrl = "magnet:?xt=urn:btih:restart-import";
    private const string TorrentName = "Recovered.Series.S01.1080p.WEB-DL";
    private const string SeriesTitle = "Recovered Series";
    private const int SeriesYear = 2021;
    private const int EpisodeCount = 4;
    private const long EpisodeSize = 3000;

    private readonly RecoveryFakes _fakes = new();
    private ServiceProvider _host = null!;
    private RecoveryDriver _driver = null!;

    private Guid _workId;
    private Guid _targetId;
    private IReadOnlyList<Guid> _episodeIds = [];

    private static string ContentPath => $"{RecoveryHost.StagingPath}/{TorrentName}";

    public async Task InitializeAsync()
    {
        (_workId, _episodeIds) = _fakes.Catalog.SeedSeries(SeriesTitle, SeriesYear, seasonNumber: 1, EpisodeCount);
        _targetId = Uuid7.New();
        _fakes.Engine.ScriptRelease(DownloadUrl, TorrentName);
        _fakes.FileSystem.SeedContent(
            ContentPath,
            [.. Enumerable.Range(1, EpisodeCount).Select(StagedEpisode)]);

        _host = await RecoveryHost.CreateAsync(Database, _fakes);
        _driver = new RecoveryDriver(_host);
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task A_pack_that_lost_one_file_is_re_driven_after_a_restart_and_lands_the_rest()
    {
        _fakes.FileSystem.FailVerifyFor(EpisodeTargetName(EpisodeCount));
        await CompleteTheDownloadAsync();

        var partial = Assert.Single(await _driver.ImportJobsAsync());
        Assert.Equal(ImportJobState.Pending, partial.State);
        Assert.Equal(3, partial.Matches.Count(m => m.IsLanded));
        Assert.Single(_fakes.Events.ImportFailed);
        Assert.Empty(_fakes.Events.ImportCompleted);

        _fakes.FileSystem.ClearFailures();
        await RestartAsync();

        var resumed = Assert.Single(await _driver.ImportJobsAsync());
        Assert.Equal(ImportJobState.Registered, resumed.State);
        Assert.Equal(EpisodeCount, resumed.Matches.Count);
        Assert.All(resumed.Matches, m => Assert.Equal(ImportFileMatchState.Registered, m.State));
        Assert.Equal(EpisodeCount, LandedFiles().Count);
        Assert.Equal(EpisodeCount, (await _driver.AssetsAsync()).Count);
        Assert.Equal(IntentState.Available, await _driver.IntentStateAsync(_targetId));
    }

    [Fact]
    public async Task The_files_that_landed_before_the_restart_are_neither_re_linked_nor_announced_twice()
    {
        _fakes.FileSystem.FailVerifyFor(EpisodeTargetName(EpisodeCount));
        await CompleteTheDownloadAsync();

        var landedBefore = LandedFiles();
        var assetIdsBefore = (await _driver.ImportJobsAsync())
            .Single().Matches.Where(m => m.IsLanded).Select(m => m.AssetId).Order().ToList();
        var linksBefore = _fakes.FileSystem.Linked.Count;

        _fakes.FileSystem.ClearFailures();
        await RestartAsync();

        // Exactly one more hardlink: the file that never landed. The three that verified are skipped,
        // which is the guard that stops a retry from re-linking a whole season pack.
        Assert.Equal(linksBefore + 1, _fakes.FileSystem.Linked.Count);
        Assert.All(landedBefore, path => Assert.True(_fakes.FileSystem.FileExists(path)));

        // One announcement per file for the whole run — three before the restart, one after. A
        // re-announced file is what would drive every downstream consumer a second time.
        Assert.Equal(EpisodeCount, _fakes.Events.MediaAvailable.Count);
        Assert.Equal(
            EpisodeCount, _fakes.Events.MediaAvailable.Select(m => m.AssetId).Distinct().Count());

        // And the asset ids minted before the restart are the ones the retry registered under: a
        // freshly minted id would collide on the library's unique path index.
        var resumed = Assert.Single(await _driver.ImportJobsAsync());
        Assert.All(assetIdsBefore, id => Assert.Contains(id, resumed.Matches.Select(m => m.AssetId)));
        Assert.Single(_fakes.Events.ImportCompleted);
    }

    [Fact]
    public async Task A_process_that_died_between_two_hardlinks_duplicates_nothing_and_loses_nothing()
    {
        // The drive dies on the fourth link, so three files are on disk and the job's single commit
        // never ran: the database says Pending with no matches at all.
        _fakes.FileSystem.ThrowOnLinkNumber(4);
        await CompleteTheDownloadAsync();

        var crashed = Assert.Single(await _driver.ImportJobsAsync());
        Assert.Equal(ImportJobState.Pending, crashed.State);
        Assert.Empty(crashed.Matches);
        Assert.Equal(3, LandedFiles().Count);
        Assert.Empty(_fakes.Events.MediaAvailable);

        _fakes.FileSystem.StopCrashing();
        await RestartAsync();

        var resumed = Assert.Single(await _driver.ImportJobsAsync());
        Assert.Equal(ImportJobState.Registered, resumed.State);
        Assert.Equal(EpisodeCount, LandedFiles().Count);
        Assert.Equal(EpisodeCount, _fakes.Events.MediaAvailable.Count);
        Assert.Equal(EpisodeCount, (await _driver.AssetsAsync()).Count);

        // The three files the dead process had already written are recognised as this job's own — the
        // same content as their sources — and kept where they are. Filing them in the bin and linking
        // them again is what a relaunched upgrade used to do, and it wrote the new file over the old copy
        // the bin was keeping.
        Assert.Empty(_fakes.FileSystem.Moved);
    }

    [Fact]
    public async Task The_re_drive_resolves_against_the_units_the_acquisition_asked_for()
    {
        // Only two of the four episodes were requested; the pack ships all four. The scope is the
        // acquisition's, and a restart that lost it would land the two nobody asked for.
        //
        // One of the two requested episodes fails to verify, and that is what makes this a test of the
        // re-drive at all: a file the scope excludes is recorded as unresolved rather than as a
        // failure, so without a genuine failure the first drive registers, recovery queues nothing,
        // and the assertions below would only be re-reading the state the arrangement produced.
        _fakes.FileSystem.FailVerifyFor(EpisodeTargetName(2));
        await CompleteTheDownloadAsync(_episodeIds.Take(2).ToList());

        // Compared as a set: the scope travels through the download task's unit rows, which have no
        // ordering of their own, so only membership is a promise.
        var job = Assert.Single(await _driver.ImportJobsAsync());
        Assert.Equal(ImportJobState.Pending, job.State);
        Assert.Equal(_episodeIds.Take(2).Order(), job.RequestedUnitIds!.Order());
        Assert.Equal([EpisodeTargetName(1)], LandedFileNames());

        _fakes.FileSystem.ClearFailures();
        await RestartAsync();

        // The re-drive really ran: the attempt counter moved and the second requested episode landed
        // afterwards, which is the state the first drive could not reach.
        var after = Assert.Single(await _driver.ImportJobsAsync());
        Assert.Equal(ImportJobState.Registered, after.State);
        Assert.Equal(1, after.RecoveryAttempts);
        Assert.Equal(2, after.Matches.Count(m => m.IsLanded));

        // And it resolved against the acquisition's scope, not against the pack: the two episodes
        // nobody asked for reached neither the library nor a single announcement, at any point in the
        // run — before the restart or after it.
        Assert.Equal([EpisodeTargetName(1), EpisodeTargetName(2)], LandedFileNames());
        Assert.Equal(2, _fakes.Events.MediaAvailable.Count);
        Assert.Equal(
            _episodeIds.Take(2).Order(),
            _fakes.Events.MediaAvailable.SelectMany(m => m.UnitIds ?? []).Distinct().Order());
    }

    [Fact]
    public async Task Without_the_recorded_scope_the_re_drive_lands_the_episodes_nobody_asked_for()
    {
        // The falsifiability control for import.import_jobs.requested_units_json. The arrangement is
        // the one above, with the recorded scope erased between the two drives — as a job written
        // before the column existed carries it. Nothing else changes, and all four episodes land.
        //
        // Without this fact the test above would be equally green if the column were never read, since
        // the first drive already constrained itself with the scope the hand-off carried.
        _fakes.FileSystem.FailVerifyFor(EpisodeTargetName(2));
        await CompleteTheDownloadAsync(_episodeIds.Take(2).ToList());

        await ForgetTheRecordedScopeAsync();
        _fakes.FileSystem.ClearFailures();
        await RestartAsync();

        // Four assets for a two-episode acquisition, against targets nobody is monitoring. This is the
        // outcome the persisted scope exists to prevent, written down as the failure it prevents.
        Assert.Equal(EpisodeCount, LandedFileNames().Count);
        Assert.Equal(EpisodeCount, _fakes.Events.MediaAvailable.Count);
    }

    [Fact]
    public async Task A_job_that_already_registered_is_not_re_driven_by_a_restart()
    {
        await CompleteTheDownloadAsync();

        var registered = Assert.Single(await _driver.ImportJobsAsync());
        Assert.Equal(ImportJobState.Registered, registered.State);
        var linksBefore = _fakes.FileSystem.Linked.Count;

        await RestartAsync();
        await RestartAsync();

        Assert.Empty(await _driver.CommandsAsync(ImportCommandNames.RetryImportJob));
        Assert.Equal(0, (await _driver.ImportJobsAsync()).Single().RecoveryAttempts);
        Assert.Equal(linksBefore, _fakes.FileSystem.Linked.Count);
        Assert.Equal(EpisodeCount, _fakes.Events.MediaAvailable.Count);
    }

    [Fact]
    public async Task Each_restart_claims_its_own_attempt_so_a_retry_is_never_dropped_as_a_duplicate()
    {
        // The last episode keeps failing to verify, so every re-drive lands nothing new and the job
        // never leaves Pending. This is the case a job-scoped idempotency key would break: the second
        // restart's command would be a duplicate of the first and the queue would drop it, silently
        // and for ever.
        _fakes.FileSystem.FailVerifyFor(EpisodeTargetName(EpisodeCount));
        await CompleteTheDownloadAsync();

        await RestartAsync();
        await RestartAsync();

        var commands = await _driver.CommandsAsync(ImportCommandNames.RetryImportJob);
        Assert.Equal(2, commands.Count);
        Assert.Equal(2, commands.Select(c => c.IdempotencyKey).Distinct().Count());

        var job = Assert.Single(await _driver.ImportJobsAsync());
        Assert.Equal(2, job.RecoveryAttempts);
        Assert.Equal(2, job.History.Count(line => line.Trigger == "RecoverPending"));
    }

    [Fact]
    public async Task A_job_that_can_never_land_stops_being_re_driven_and_is_left_for_a_person()
    {
        // The last episode never verifies, for a reason no restart can change — a permission the
        // process does not have, a disk with no room. Without a ceiling this job is re-driven at every
        // start for the life of the installation, appending a history line and re-scanning the whole
        // staging tree each time, with nothing in its state to say it will never succeed.
        _fakes.FileSystem.FailVerifyFor(EpisodeTargetName(EpisodeCount));
        await CompleteTheDownloadAsync();

        for (var restart = 0; restart <= ImportJob.MaxRecoveryAttempts; restart++)
        {
            await RestartAsync();
        }

        var job = Assert.Single(await _driver.ImportJobsAsync());
        Assert.Equal(ImportJobState.Unmatched, job.State);
        Assert.Equal(ImportJob.MaxRecoveryAttempts, job.RecoveryAttempts);
        Assert.Single(job.History, line => line.Trigger == "RecoveryExhausted");
        Assert.False(string.IsNullOrWhiteSpace(job.Reason), "the job was parked without saying why");

        // Parking is not losing: what landed is still landed, and still announced exactly once.
        Assert.Equal(EpisodeCount - 1, LandedFileNames().Count);
        Assert.Equal(EpisodeCount - 1, _fakes.Events.MediaAvailable.Count);

        // And the next start leaves it alone rather than queueing a sixth attempt.
        var queuedBefore = (await _driver.CommandsAsync(ImportCommandNames.RetryImportJob)).Count;
        await RestartAsync();
        Assert.Equal(queuedBefore, (await _driver.CommandsAsync(ImportCommandNames.RetryImportJob)).Count);
        Assert.Equal(ImportJob.MaxRecoveryAttempts, (await _driver.ImportJobsAsync()).Single().RecoveryAttempts);
    }

    [Fact]
    public async Task A_job_whose_source_path_left_the_staging_root_is_not_re_driven()
    {
        _fakes.FileSystem.FailVerifyFor(EpisodeTargetName(EpisodeCount));
        await CompleteTheDownloadAsync();

        // The path a re-drive replays is composed from the torrent's own name and is read back out of
        // the database at every start — possibly out of a restored or edited row. Pointed outside the
        // staging root it would make the import scan an arbitrary directory and hardlink whatever
        // passes the policy into the household's library, under the operator's own catalogue title.
        const string outside = "/srv/not-the-staging-root";
        _fakes.FileSystem.SeedContent(
            outside,
            new ImportFileEntry($"{outside}/Recovered.Series.S01E{EpisodeCount:D2}.1080p.WEB-DL.mkv", EpisodeSize));
        await RepointTheSourcePathAsync(outside);
        _fakes.FileSystem.ClearFailures();
        var linkedBefore = _fakes.FileSystem.Linked.Count;

        await RestartAsync();

        // The re-drive was queued and refused before anything was read: no scan, no hardlink, and the
        // episode that never landed still has not.
        Assert.Equal(linkedBefore, _fakes.FileSystem.Linked.Count);
        Assert.Equal(EpisodeCount - 1, LandedFileNames().Count);
        Assert.DoesNotContain(EpisodeTargetName(EpisodeCount), LandedFileNames());
    }

    // -- helpers ---------------------------------------------------------------------------------

    private static ImportFileEntry StagedEpisode(int number) =>
        new($"{ContentPath}/Recovered.Series.S01E{number:D2}.1080p.WEB-DL.x264.mkv", EpisodeSize);

    /// <summary>
    /// The library file name one episode lands under. Composed from catalogue data rather than from
    /// the staged name, which is what the organiser does — so naming the file this way is also how a
    /// test says "this episode and no other".
    /// </summary>
    private static string EpisodeTargetName(int number) =>
        $"{SeriesTitle} - S01E{number:D2} - Episode {number}.mkv";

    /// <summary>Every file in the library, excluding the recycle bin that lives under the same root.</summary>
    private IReadOnlyList<string> LandedFiles() =>
        [.. _fakes.FileSystem.FilesUnder(RecoveryHost.LibraryRoot)
            .Where(path => !path.Contains(".recycle", StringComparison.Ordinal))];

    /// <summary>The landed files by name, ordered, so a test can name exactly which episodes are there.</summary>
    private IReadOnlyList<string> LandedFileNames() =>
        [.. LandedFiles().Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal)];

    /// <summary>
    /// Erases the acquisition scope the job recorded, leaving everything else exactly as it was. An
    /// installation upgraded mid-import has jobs in precisely this shape, and it is the only way to
    /// ask whether the column is what constrains the re-drive.
    /// </summary>
    private async Task ForgetTheRecordedScopeAsync()
    {
        await using var scope = _host.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ImportDbContext>();
        var cleared = await dbContext.Database.ExecuteSqlAsync(
            $"UPDATE import.import_jobs SET requested_units_json = NULL");
        Assert.Equal(1, cleared);
    }

    /// <summary>
    /// Points the job at another directory, which is what a torrent name shaped to escape the staging
    /// tree, a restored backup or an edited row all amount to by the time recovery replays the path.
    /// </summary>
    private async Task RepointTheSourcePathAsync(string sourcePath)
    {
        await using var scope = _host.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ImportDbContext>();
        var repointed = await dbContext.Database.ExecuteSqlAsync(
            $"UPDATE import.import_jobs SET source_path = {sourcePath}");
        Assert.Equal(1, repointed);
    }

    /// <summary>
    /// Carries the acquisition through to a finished download, which is what hands the pack to Import.
    /// The import that follows is whatever the filesystem fake was told to do to it.
    /// </summary>
    private async Task CompleteTheDownloadAsync(IReadOnlyList<Guid>? unitIds = null)
    {
        await _driver.AcquireAsync(_targetId, _workId, ReleaseGuid, DownloadUrl, unitIds ?? _episodeIds);
        await _driver.ApplyStatusAsync(_fakes.Engine.SnapshotFor(DownloadUrl, "downloading"));
        await _driver.ApplyStatusAsync(_fakes.Engine.SnapshotFor(DownloadUrl, "finished", isFinished: true));
    }

    private async Task RestartAsync()
    {
        _host = await RecoveryHost.RestartAsync(_host, Database, _fakes);
        _driver = new RecoveryDriver(_host);
        await _driver.DrainAsync();
    }
}
