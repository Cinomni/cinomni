using Cinomni.Acquisition.Contracts;
using Cinomni.Downloads.Contracts;
using Cinomni.Import.Contracts;
using Cinomni.Import.Files;
using Cinomni.Import.Persistence;
using Cinomni.Kernel.Identifiers;
using Cinomni.Kernel.Messaging;
using Cinomni.Library.Contracts;
using Cinomni.Library.Persistence;
using Cinomni.Operations.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Import.Tests;

/// <summary>
/// What happens on disk when a better release lands for something the library already holds. An
/// episode's library path is composed from its numbering, so the replacement arrives at exactly the
/// path the current copy occupies — and the landing would delete it to make room. These pin the
/// promise that the household's file is set aside, not destroyed.
/// </summary>
public sealed class UpgradeReplacementTests : IAsyncLifetime
{
    /// <summary>The last segment of the configured library root, comparable on any host.</summary>
    private const string LibraryFolder = "test-library";
    private const string FirstContent = "/data/staging/Movie.2024.720p";
    private const string FirstFile = "/data/staging/Movie.2024.720p/Movie.2024.720p.WEB-DL.x264.mkv";
    private const string BetterContent = "/data/staging/Movie.2024.1080p";
    private const string BetterFile = "/data/staging/Movie.2024.1080p/Movie.2024.720p.WEB-DL.x264.mkv";

    private readonly FakeImportFileSystem _fileSystem = new();
    private readonly FakeMediaProbe _mediaProbe = new();
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync() =>
        _provider = await ImportTestHost.CreateAsync("cinomni_test_import_upgrade", _fileSystem, _mediaProbe);

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task The_superseded_file_is_set_aside_rather_than_overwritten()
    {
        var workId = Uuid7.New();
        await ImportAsync(workId, FirstContent, FirstFile, size: 1000);
        var landed = Assert.Single(_fileSystem.Linked).To;

        // The same target path, because the layout is derived from the title, not from the release.
        await ImportAsync(workId, BetterContent, BetterFile, size: 4000);

        var recycled = Assert.Single(_fileSystem.Moved);
        Assert.Equal(landed, recycled.From);
        // Compared by segment, not by whole path: PathGuard returns the host's own separators.
        Assert.Contains(LibraryFolder, recycled.To);
        Assert.Contains(".recycle", recycled.To);

        // And it is still there: an upgrade is not a deletion.
        Assert.True(_fileSystem.FileExists(recycled.To));
        Assert.Equal(1000, _fileSystem.GetSize(recycled.To));
    }

    [Fact]
    public async Task The_replacement_is_the_file_that_ends_up_in_the_library()
    {
        var workId = Uuid7.New();
        await ImportAsync(workId, FirstContent, FirstFile, size: 1000);
        var landed = Assert.Single(_fileSystem.Linked).To;

        await ImportAsync(workId, BetterContent, BetterFile, size: 4000);

        Assert.True(_fileSystem.FileExists(landed));
        Assert.Equal(4000, _fileSystem.GetSize(landed));
    }

    [Fact]
    public async Task The_recycle_step_is_recorded_as_its_own_recoverable_operation()
    {
        // Not a silent side effect of the landing: the job's trail has to explain where a file the
        // household owns went.
        var workId = Uuid7.New();
        await ImportAsync(workId, FirstContent, FirstFile, size: 1000);
        var secondTask = await ImportAsync(workId, BetterContent, BetterFile, size: 4000);

        var job = await JobAsync(secondTask);

        var recycle = Assert.Single(job.Operations, o => o.Type == FileOperationType.Recycle);
        Assert.True(recycle.Verified);
        Assert.Contains(".recycle", recycle.ToPath);
    }

    /// <summary>
    /// The crash window: an earlier attempt at the upgrade set the previous copy aside, landed the
    /// better file and died before recording either. The library path already holds the better file.
    /// Treating it as a copy to replace used to move it into the bin on top of the previous copy —
    /// destroying the one file the bin existed to keep.
    /// </summary>
    [Fact]
    public async Task A_relaunched_upgrade_finds_its_own_landing_and_leaves_the_bin_alone()
    {
        var workId = Uuid7.New();
        await ImportAsync(workId, FirstContent, FirstFile, size: 1000);
        var target = Assert.Single(_fileSystem.Linked).To;

        _fileSystem.SetFingerprint(BetterFile, "fp-better");
        _fileSystem.SeedFile(target, 4000, fingerprint: "fp-better");

        await ImportAsync(workId, BetterContent, BetterFile, size: 4000);

        Assert.DoesNotContain(_fileSystem.Moved, move => move.From == target);
        Assert.Equal(4000, _fileSystem.GetSize(target));
        Assert.Equal(workId, (await OccupantAsync(target))!.WorkId);
    }

    /// <summary>
    /// An upgrade lands on the path of the version it replaces, and Library's unique path used to count
    /// the retired version too: the registration failed on every retry and the upgrade never appeared.
    /// </summary>
    [Fact]
    public async Task An_upgrade_on_the_same_path_is_registered_and_the_old_asset_retired()
    {
        var workId = Uuid7.New();
        await ImportAsync(workId, FirstContent, FirstFile, size: 1000);
        var target = Assert.Single(_fileSystem.Linked).To;
        var first = await OccupantAsync(target);
        Assert.NotNull(first);

        await ImportAsync(workId, BetterContent, BetterFile, size: 4000);

        var current = await OccupantAsync(target);
        Assert.NotNull(current);
        Assert.NotEqual(first!.Id, current!.Id);
        await using var scope = _provider.CreateAsyncScope();
        var previous = await scope.ServiceProvider.GetRequiredService<ILibraryQuery>().GetAsync(first.Id);
        Assert.Equal(MediaAssetState.Upgraded, previous!.Asset.State);
    }

    /// <summary>
    /// Two works that share a title and a year — or, as here, a release file name — compose the same
    /// library path. The second used to recycle the first title's film as if it were an older copy of
    /// itself and take its place, so whoever was allowed to see the first played the second.
    /// </summary>
    [Fact]
    public async Task Two_titles_with_the_same_file_name_never_share_a_file()
    {
        var firstWork = Uuid7.New();
        var secondWork = Uuid7.New();
        await ImportAsync(firstWork, FirstContent, FirstFile, size: 1000);
        var firstTarget = Assert.Single(_fileSystem.Linked).To;

        await ImportAsync(secondWork, BetterContent, BetterFile, size: 4000);

        Assert.Empty(_fileSystem.Moved);
        var secondTarget = _fileSystem.Linked[^1].To;
        Assert.NotEqual(firstTarget, secondTarget);
        Assert.Contains($"[{LibraryOrganizer.OwnerTag(secondWork)}]", secondTarget, StringComparison.Ordinal);
        Assert.Equal(1000, _fileSystem.GetSize(firstTarget));
        Assert.Equal(firstWork, (await OccupantAsync(firstTarget))!.WorkId);
        Assert.Equal(secondWork, (await OccupantAsync(secondTarget))!.WorkId);
    }

    /// <summary>
    /// Two downloads finishing together: Library only hears of the first landing once its event has been
    /// relayed and its command run, so asking Library alone let the second see an empty folder and take
    /// it. Import's own record of what it landed is written before the next import starts.
    /// </summary>
    [Fact]
    public async Task A_title_landed_moments_ago_is_not_taken_over_before_library_has_heard_of_it()
    {
        var firstWork = Uuid7.New();
        var secondWork = Uuid7.New();
        await ImportAsync(firstWork, FirstContent, FirstFile, size: 1000);
        var firstTarget = Assert.Single(_fileSystem.Linked).To;
        await ForgetInLibraryAsync(firstWork);

        await ImportAsync(secondWork, BetterContent, BetterFile, size: 4000);

        Assert.Empty(_fileSystem.Moved);
        Assert.Contains($"[{LibraryOrganizer.OwnerTag(secondWork)}]", _fileSystem.Linked[^1].To, StringComparison.Ordinal);
        Assert.Equal(1000, _fileSystem.GetSize(firstTarget));
    }

    [Fact]
    public async Task A_title_that_lives_under_its_own_tag_is_upgraded_there()
    {
        var firstWork = Uuid7.New();
        var secondWork = Uuid7.New();
        await ImportAsync(firstWork, FirstContent, FirstFile, size: 1000);
        var firstTarget = _fileSystem.Linked[^1].To;
        await ImportAsync(secondWork, BetterContent, BetterFile, size: 2000);
        var tagged = _fileSystem.Linked[^1].To;

        const string upgradeContent = "/data/staging/Movie.2024.2160p";
        await ImportAsync(secondWork, upgradeContent, upgradeContent + "/Movie.2024.720p.WEB-DL.x264.mkv", size: 8000);

        // The upgrade finds the title's own folder, sets its previous copy aside and takes its place.
        Assert.Equal(tagged, _fileSystem.Linked[^1].To);
        Assert.Equal(8000, _fileSystem.GetSize(tagged));
        var recycled = Assert.Single(_fileSystem.Moved);
        Assert.Equal(tagged, recycled.From);
        Assert.Equal(2000, _fileSystem.GetSize(recycled.To));
        Assert.Equal(1000, _fileSystem.GetSize(firstTarget));
    }

    [Fact]
    public async Task A_title_whose_plain_and_tagged_folders_both_belong_to_others_is_refused_without_touching_either()
    {
        var firstWork = Uuid7.New();
        var squatter = Uuid7.New();
        var secondWork = Uuid7.New();
        await ImportAsync(firstWork, FirstContent, FirstFile, size: 1000);

        // A release named exactly like the second title's tagged folder lands there first.
        var tagged = $"Movie.2024.720p.WEB-DL.x264 [{LibraryOrganizer.OwnerTag(secondWork)}]";
        await ImportAsync(squatter, "/data/staging/squat", $"/data/staging/squat/{tagged}.mkv", size: 3000);
        var linkedBefore = _fileSystem.Linked.Count;

        var task = await ImportAsync(secondWork, BetterContent, BetterFile, size: 4000);

        Assert.Equal(linkedBefore, _fileSystem.Linked.Count);
        Assert.Empty(_fileSystem.Moved);
        var refused = Assert.Single((await JobAsync(task)).Matches);
        Assert.Equal(ImportFileMatchState.Failed, refused.State);
        Assert.Contains("another title", refused.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_bin_numbers_a_second_copy_rather_than_write_over_the_first()
    {
        var workId = Uuid7.New();
        await ImportAsync(workId, FirstContent, FirstFile, size: 1000);
        var target = _fileSystem.Linked[^1].To;
        // The stamped name this job would use is already taken in the bin.
        _fileSystem.AlsoExists = path => path.Contains(".recycle", StringComparison.Ordinal)
            && !path.Contains("-2-", StringComparison.Ordinal);

        await ImportAsync(workId, BetterContent, BetterFile, size: 4000);

        var recycled = Assert.Single(_fileSystem.Moved);
        Assert.Equal(target, recycled.From);
        Assert.Contains("-2-", recycled.To, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_bin_with_no_free_name_left_is_a_refusal_not_an_overwrite()
    {
        var workId = Uuid7.New();
        await ImportAsync(workId, FirstContent, FirstFile, size: 1000);
        var target = _fileSystem.Linked[^1].To;
        _fileSystem.AlsoExists = path => path.Contains(".recycle", StringComparison.Ordinal);

        var task = await ImportAsync(workId, BetterContent, BetterFile, size: 4000);

        Assert.Empty(_fileSystem.Moved);
        Assert.Equal(1000, _fileSystem.GetSize(target));
        Assert.Equal(ImportFileMatchState.Failed, Assert.Single((await JobAsync(task)).Matches).State);
    }

    [Fact]
    public async Task A_file_of_the_same_size_but_other_content_is_set_aside_rather_than_taken_for_this_landing()
    {
        var workId = Uuid7.New();
        await ImportAsync(workId, FirstContent, FirstFile, size: 4000);
        var target = _fileSystem.Linked[^1].To;

        await ImportAsync(workId, BetterContent, BetterFile, size: 4000);

        var recycled = Assert.Single(_fileSystem.Moved);
        Assert.Equal(target, recycled.From);
    }

    [Fact]
    public async Task A_file_that_cannot_be_read_at_the_library_path_fails_that_file_and_nothing_else()
    {
        var workId = Uuid7.New();
        await ImportAsync(workId, FirstContent, FirstFile, size: 1000);
        var target = _fileSystem.Linked[^1].To;
        _fileSystem.Unreadable.Add(target);

        var task = await ImportAsync(workId, BetterContent, BetterFile, size: 4000);

        var match = Assert.Single((await JobAsync(task)).Matches);
        Assert.Equal(ImportFileMatchState.Failed, match.State);
        Assert.Empty(_fileSystem.Moved);
        Assert.Equal(1000, _fileSystem.GetSize(target));
    }

    [Fact]
    public async Task A_first_import_recycles_nothing()
    {
        var task = await ImportAsync(Uuid7.New(), FirstContent, FirstFile, size: 1000);

        Assert.Empty(_fileSystem.Moved);
        Assert.DoesNotContain((await JobAsync(task)).Operations, o => o.Type == FileOperationType.Recycle);
    }

    /// <summary>Runs one whole import for a work and returns its download task id.</summary>
    private async Task<Guid> ImportAsync(Guid workId, string contentPath, string filePath, long size)
    {
        var targetId = Uuid7.New();
        var (intentId, attemptId) = await ArrangeIntentAsync(workId, targetId);
        _fileSystem.SeedContent(contentPath, new ImportFileEntry(filePath, size));

        var downloadTaskId = Uuid7.New();
        await using (var scope = _provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<IEventHandler<DownloadCompleted>>();
            await handler.HandleAsync(new DownloadCompleted(
                downloadTaskId, intentId, attemptId, workId, targetId, "infohash", contentPath));
        }

        await DrainAsync();
        return downloadTaskId;
    }

    /// <summary>An acquisition goal driven to Importing, as a completed download would leave it.</summary>
    private async Task<(Guid IntentId, Guid AttemptId)> ArrangeIntentAsync(Guid workId, Guid targetId)
    {
        await using (var scope = _provider.CreateAsyncScope())
        {
            var commands = scope.ServiceProvider.GetRequiredService<IAcquisitionCommands>();
            await commands.CreateIntentAsync(targetId, workId, "All");
            await commands.SelectCandidateAsync(Uuid7.New(), targetId, "g1", "magnet:?xt=urn:btih:g1");
        }

        Guid intentId;
        Guid attemptId;
        await using (var scope = _provider.CreateAsyncScope())
        {
            var query = scope.ServiceProvider.GetRequiredService<IAcquisitionQuery>();
            var intent = await query.GetByTargetAsync(targetId);
            var detail = await query.GetAsync(intent!.Id.Value);
            (intentId, attemptId) = (intent.Id.Value, detail!.Attempts.Single().Id.Value);
        }

        await using (var scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IAcquisitionCommands>().MarkDownloadCompletedAsync(intentId);
        }

        return (intentId, attemptId);
    }

    /// <summary>What Library knows of a work, gone — as if its registration had not been processed yet.</summary>
    private async Task ForgetInLibraryAsync(Guid workId)
    {
        await using var scope = _provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<LibraryDbContext>().Assets
            .Where(a => a.WorkId == workId)
            .ExecuteDeleteAsync();
    }

    private async Task<MediaAssetSummary?> OccupantAsync(string path)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ILibraryQuery>().FindActiveByPathAsync(path);
    }

    private async Task<ImportJob> JobAsync(Guid downloadTaskId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var job = await scope.ServiceProvider.GetRequiredService<ImportDbContext>()
            .Jobs.Include(j => j.Operations).Include(j => j.Matches)
            .FirstOrDefaultAsync(j => j.DownloadTaskId == downloadTaskId);
        Assert.NotNull(job);
        return job!;
    }

    /// <summary>Drives the relay and the command queue to a standstill, as the host's workers would.</summary>
    private async Task DrainAsync()
    {
        while (true)
        {
            var commands = await DrainAsync<CommandProcessor>(p => p.ProcessBatchAsync());
            var events = await DrainAsync<OutboxRelay>(r => r.ProcessBatchAsync());
            if (commands == 0 && events == 0)
            {
                return;
            }
        }
    }

    private async Task<int> DrainAsync<T>(Func<T, Task<int>> batch) where T : notnull
    {
        await using var scope = _provider.CreateAsyncScope();
        var worker = scope.ServiceProvider.GetRequiredService<T>();
        var total = 0;
        int handled;
        while ((handled = await batch(worker)) > 0)
        {
            total += handled;
        }

        return total;
    }
}
