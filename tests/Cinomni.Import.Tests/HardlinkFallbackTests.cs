using Cinomni.Downloads.Contracts;
using Cinomni.Import.Contracts;
using Cinomni.Import.Files;
using Cinomni.Import.Persistence;
using Cinomni.Kernel.Identifiers;
using Cinomni.Kernel.Messaging;
using Cinomni.Operations.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Import.Tests;

/// <summary>
/// What the job records when the library gets a copy instead of a hardlink. A hardlink is only possible
/// inside one filesystem and only for an account allowed to make it, so a deployment that mounts the
/// library and the download staging directory separately — or runs the two containers as two accounts —
/// gets a full byte-for-byte copy of every import (DEPLOYMENT.md, "The storage contract").
/// <para>
/// That is allowed to happen. What is not allowed is the installation asserting a hardlink it did not
/// make: the operation row is the household's own record of what happened to its file, and a copy that
/// says "Hardlink" tells an operator the bytes are shared when deleting the download would leave the
/// library holding the only copy — or the disk twice as full as the record implies.
/// </para>
/// <para>
/// The same rule produces a third outcome that only a resumed job can reach. A file an interrupted
/// attempt already landed is asked which of the two it was, and the filesystem may refuse to say; the
/// record then says <c>Unknown</c> rather than the likelier one, and the installation says so out loud,
/// because "the record does not know" is a thing an operator has to be able to find out.
/// </para>
/// </summary>
public sealed class HardlinkFallbackTests : IAsyncLifetime
{
    private const string ContentPath = "/data/staging/The.Wire.S01.1080p.BluRay.x264";
    private const long EpisodeSize = 2000;

    /// <summary>The last segment of the configured library root, as a landed path would contain it.</summary>
    private const string LibraryFolder = "test-library";

    private readonly FakeImportFileSystem _fileSystem = new();
    private readonly FakeMediaProbe _mediaProbe = new();
    private readonly FakeCatalogSeriesQuery _catalog = new();
    private LogCapture _logs = null!;
    private ServiceProvider _provider = null!;

    private Guid _workId;
    private IReadOnlyList<Guid> _episodeIds = [];

    public async Task InitializeAsync()
    {
        (_workId, _episodeIds) = _catalog.SeedSeries("The Wire", 2002, seasonNumber: 1, episodeCount: 3);
        _provider = await ImportTestHost.CreateAsync(
            "cinomni_test_import_hardlink_fallback",
            _fileSystem,
            _mediaProbe,
            services => _logs = LogCapture.Register(services),
            _catalog);
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task A_file_the_adapter_copied_is_recorded_as_a_copy()
    {
        _fileSystem.FallsBackToCopy = true;
        SeedPack(1, 2);

        var job = await ImportPackAsync();

        Assert.Equal(2, job.Operations.Count);
        Assert.All(job.Operations, operation => Assert.Equal(FileOperationType.Copy, operation.Type));
        Assert.All(job.Operations, operation => Assert.True(operation.Verified));
    }

    [Fact]
    public async Task A_file_the_adapter_hardlinked_is_still_recorded_as_a_hardlink()
    {
        SeedPack(1, 2);

        var job = await ImportPackAsync();

        Assert.Equal(2, job.Operations.Count);
        Assert.All(job.Operations, operation => Assert.Equal(FileOperationType.Hardlink, operation.Type));
    }

    [Fact]
    public async Task The_broken_storage_contract_is_reported_once_per_job_naming_the_two_configuration_keys()
    {
        _fileSystem.FallsBackToCopy = true;
        SeedPack(1, 2, 3);

        await ImportPackAsync();

        // Once for the job and not once per file: a season pack would otherwise repeat one
        // installation-wide fact three times and train the operator to skip it.
        var warning = Assert.Single(_logs.Warnings, message => message.Contains("instead of hardlinking"));
        Assert.Contains("Downloads:Sidecar:StagingPath", warning, StringComparison.Ordinal);
        Assert.Contains("Import:LibraryRoot", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_report_names_no_path_of_its_own()
    {
        // A staging path is private layout and a library path spells out what the household watches.
        _fileSystem.FallsBackToCopy = true;
        SeedPack(1);

        await ImportPackAsync();

        var warning = Assert.Single(_logs.Warnings, message => message.Contains("instead of hardlinking"));
        Assert.DoesNotContain(ContentPath, warning, StringComparison.Ordinal);
        Assert.DoesNotContain(LibraryFolder, warning, StringComparison.Ordinal);
        Assert.DoesNotContain("The Wire", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_installation_whose_hardlinks_work_says_nothing_about_copies()
    {
        SeedPack(1, 2);

        await ImportPackAsync();

        Assert.DoesNotContain(_logs.Warnings, message => message.Contains("instead of hardlinking"));
    }

    /// <summary>
    /// The third outcome, and the only one a fresh import cannot produce: a job resumed after a restart
    /// reaches a file an interrupted attempt already landed, and the filesystem refuses the identity that
    /// says whether it shares its bytes with the download. The record must carry that through to the
    /// database rather than settle on the likelier of the two — the enum name is what is stored, so this
    /// also proves the value survives a real write and read.
    /// </summary>
    [Fact]
    public async Task A_landing_the_platform_will_not_explain_is_stored_and_read_back_as_unknown()
    {
        _fileSystem.LandingCannotBeIdentified = true;
        SeedPack(1, 2);

        var job = await ImportPackAsync();

        Assert.Equal(2, job.Operations.Count);
        Assert.All(job.Operations, operation => Assert.Equal(FileOperationType.Unknown, operation.Type));
        // Unknown is about which operation put the file there, not about whether it is there: the file
        // was found in place and intact, so the job goes on to register it.
        Assert.All(job.Operations, operation => Assert.True(operation.Verified));
    }

    /// <summary>
    /// The persisted row is not enough on its own: it is one row of one job, and nothing surfaces it. An
    /// operator deciding whether removing a download frees the disk or destroys the household's only copy
    /// has to be told the installation does not know.
    /// </summary>
    [Fact]
    public async Task A_landing_the_platform_will_not_explain_is_reported_once_per_job()
    {
        _fileSystem.LandingCannotBeIdentified = true;
        SeedPack(1, 2, 3);

        await ImportPackAsync();

        // Once for the job and not once per file, for the same reason the copy report is: a season pack
        // would otherwise repeat one fact three times and train the operator to skip it.
        var warning = Assert.Single(_logs.Warnings, message => message.Contains("recorded as Unknown"));
        // What could not be determined, in the words an operator can act on.
        Assert.Contains("shares its bytes with the download", warning, StringComparison.Ordinal);
        // And never the paths: a staging path is private layout, a library path spells out the title.
        Assert.DoesNotContain(ContentPath, warning, StringComparison.Ordinal);
        Assert.DoesNotContain(LibraryFolder, warning, StringComparison.Ordinal);
        Assert.DoesNotContain("The Wire", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_landing_that_could_not_be_identified_and_then_failed_to_verify_is_not_reported()
    {
        // The report is about a file that is in the library and whose provenance nothing can explain. A
        // landing that failed to verify was rolled back, so there is no such file and nothing to act on.
        _fileSystem.LandingCannotBeIdentified = true;
        _fileSystem.FailVerify = true;
        SeedPack(1);

        await ImportPackAsync();

        Assert.DoesNotContain(_logs.Warnings, message => message.Contains("recorded as Unknown"));
    }

    [Fact]
    public async Task An_import_that_landed_every_file_itself_says_nothing_about_unknown_landings()
    {
        // The other half of the promise: a report that appears on ordinary imports stops meaning anything.
        SeedPack(1, 2);

        await ImportPackAsync();

        Assert.DoesNotContain(_logs.Warnings, message => message.Contains("recorded as Unknown"));
    }

    // -- helpers ---------------------------------------------------------------------------------

    private static ImportFileEntry Entry(int episode) =>
        new($"{ContentPath}/The.Wire.S01E{episode:D2}.1080p.BluRay.x264.mkv", EpisodeSize);

    private void SeedPack(params int[] episodes) =>
        _fileSystem.SeedContent(ContentPath, [.. episodes.Select(Entry)]);

    /// <summary>Runs one whole import of the seeded pack and returns the persisted job.</summary>
    private async Task<ImportJob> ImportPackAsync()
    {
        var downloadTaskId = Uuid7.New();
        await using (var scope = _provider.CreateAsyncScope())
        {
            foreach (var handler in scope.ServiceProvider.GetServices<IEventHandler<DownloadCompleted>>())
            {
                await handler.HandleAsync(new DownloadCompleted(
                    downloadTaskId,
                    Guid.Empty,
                    Guid.Empty,
                    _workId,
                    Uuid7.New(),
                    "infohash",
                    ContentPath,
                    UnitIds: _episodeIds));
            }
        }

        await DrainAsync();

        await using var reading = _provider.CreateAsyncScope();
        var job = await reading.ServiceProvider.GetRequiredService<ImportDbContext>()
            .Jobs
            .AsNoTracking()
            .Include(j => j.Operations)
            .Include(j => j.Matches)
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

    private async Task<int> DrainAsync<T>(Func<T, Task<int>> batch)
        where T : notnull
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
