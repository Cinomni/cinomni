using Cinomni.Downloads.Contracts;
using Cinomni.Import.Contracts;
using Cinomni.Import.Files;
using Cinomni.Kernel.Identifiers;
using Cinomni.Operations.Persistence;
using Cinomni.Recovery.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Recovery.Tests;

/// <summary>
/// The platform properties the fifteen scenarios rest on, exercised across a real restart.
/// <para>
/// None of these is a numbered scenario, and each of them is a way several of the fifteen would fail
/// without anyone being able to say why: an event that was committed and never delivered, a command
/// a crash left claimed by a process that no longer exists, a schedule that resets its clock at every
/// start, a delivery that happens twice. <c>RecoverOperationsAsync</c> has existed since the Operations platform module and,
/// until this suite, was never once called by a test.
/// </para>
/// </summary>
public sealed class PlatformRestartRecoveryTests : IAsyncLifetime
{
    private const string Database = "cinomni_test_recovery_platform";
    private const string ReleaseGuid = "release-platform";
    private const string DownloadUrl = "magnet:?xt=urn:btih:platform";
    private const string TorrentName = "Platform.Feature.2023.1080p.WEB-DL";

    private readonly RecoveryFakes _fakes = new();
    private ServiceProvider _host = null!;
    private RecoveryDriver _driver = null!;

    private Guid _workId;
    private Guid _targetId;

    public async Task InitializeAsync()
    {
        _workId = _fakes.Catalog.SeedMovie("Platform Feature", 2023);
        _targetId = Uuid7.New();
        _fakes.Engine.ScriptRelease(DownloadUrl, TorrentName);
        _fakes.FileSystem.SeedContent(
            $"{RecoveryHost.StagingPath}/{TorrentName}",
            new ImportFileEntry($"{RecoveryHost.StagingPath}/{TorrentName}/{TorrentName}.mkv", 5000));

        _host = await RecoveryHost.CreateAsync(Database, _fakes);
        _driver = new RecoveryDriver(_host);
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task An_event_a_crash_left_unpublished_is_delivered_after_the_restart()
    {
        await _driver.AcquireAsync(_targetId, _workId, ReleaseGuid, DownloadUrl, [_workId]);
        await _driver.ApplyStatusAsync(_fakes.Engine.SnapshotFor(DownloadUrl, "downloading"));

        // The download finishes and its event commits with the task's state — and then the process
        // dies before the relay ever runs. This is the whole reason the outbox exists, and nothing
        // asserted it survived a restart until now.
        await ApplyStatusWithoutRelayingAsync(
            _fakes.Engine.SnapshotFor(DownloadUrl, "finished", isFinished: true));

        Assert.True(await _driver.UnpublishedOutboxCountAsync() > 0);
        Assert.Empty(_fakes.Events.DownloadCompleted);

        await RestartAsync();

        Assert.Equal(0, await _driver.UnpublishedOutboxCountAsync());
        Assert.Single(_fakes.Events.DownloadCompleted);
        Assert.Single(await _driver.AssetsAsync());
    }

    [Fact]
    public async Task A_command_a_crash_left_running_is_requeued_and_runs_exactly_once()
    {
        await _driver.AcquireAsync(_targetId, _workId, ReleaseGuid, DownloadUrl, [_workId]);
        await _driver.ApplyStatusAsync(_fakes.Engine.SnapshotFor(DownloadUrl, "downloading"));
        await ApplyStatusWithoutRelayingAsync(
            _fakes.Engine.SnapshotFor(DownloadUrl, "finished", isFinished: true));

        // Deliver the event but leave the import command claimed, which is exactly what a process that
        // is killed mid-handler leaves behind: a row that says Running and nothing running it.
        await _driver.DrainOutboxAsync();
        await StrandTheImportCommandAsync();

        await RestartAsync();

        var job = Assert.Single(await _driver.ImportJobsAsync());
        Assert.Equal(ImportJobState.Registered, job.State);
        Assert.Single(_fakes.Events.MediaAvailable);
        Assert.Single(_fakes.FileSystem.Linked);
    }

    [Fact]
    public async Task A_redelivered_event_after_a_restart_changes_nothing_a_second_time()
    {
        await _driver.AcquireAsync(_targetId, _workId, ReleaseGuid, DownloadUrl, [_workId]);
        await _driver.ApplyStatusAsync(_fakes.Engine.SnapshotFor(DownloadUrl, "downloading"));
        await _driver.ApplyStatusAsync(_fakes.Engine.SnapshotFor(DownloadUrl, "finished", isFinished: true));

        var assetsBefore = await _driver.AssetsAsync();
        Assert.Single(assetsBefore);

        await RestartAsync();
        await ReplayEveryPublishedEventAsync();
        await _driver.DrainAsync();

        // The outbox is at-least-once by design, so this is the shape of a real redelivery: the same
        // rows, delivered again, after a restart. Every consumer has to absorb it.
        var assetsAfter = await _driver.AssetsAsync();
        Assert.Equal(assetsBefore.Select(a => a.Id).Order(), assetsAfter.Select(a => a.Id).Order());
        Assert.Single(_fakes.FileSystem.Linked);
        Assert.Single(await _driver.ImportJobsAsync());
    }

    [Fact]
    public async Task A_restart_re_registers_the_scheduled_jobs_without_resetting_their_clock()
    {
        // One start first: the schedule rows are written by recovery itself, so an installation that
        // has never started has none to preserve and the assertion below would pass vacuously.
        await RestartAsync();

        var ranAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var dueAt = DateTimeOffset.UtcNow.AddHours(3);
        await using (var scope = _host.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
            var updated = await dbContext.ScheduledJobs
                .Where(job => job.Name == CheckpointJob)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(job => job.LastRun, ranAt)
                    .SetProperty(job => job.NextDue, dueAt));
            Assert.Equal(1, updated);
        }

        await RestartAsync();

        var checkpoint = await ScheduledJobAsync(CheckpointJob);
        Assert.NotNull(checkpoint);
        Assert.True(checkpoint!.Enabled);

        // Re-registering must sync the definition and leave the history: a restart that reset NextDue
        // would run every job in the installation at every start, which on a short-cadence job is a
        // burst of work nobody asked for.
        Assert.Equal(dueAt, checkpoint.NextDue, TimeSpan.FromSeconds(1));
        Assert.Equal(ranAt, checkpoint.LastRun!.Value, TimeSpan.FromSeconds(1));
    }

    // -- helpers ---------------------------------------------------------------------------------

    /// <summary>The Downloads checkpoint job, which every host in this suite registers.</summary>
    private const string CheckpointJob = "downloads.checkpoint";

    private async Task<ScheduledJob?> ScheduledJobAsync(string name)
    {
        await using var scope = _host.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OperationsDbContext>()
            .ScheduledJobs.AsNoTracking().FirstOrDefaultAsync(job => job.Name == name);
    }

    /// <summary>
    /// Applies a snapshot and stops. The module's write and its outbox row commit together, and the
    /// relay that would deliver the row is never turned — which is a crash between the two.
    /// </summary>
    private async Task ApplyStatusWithoutRelayingAsync(Downloads.Engine.TorrentSnapshot snapshot)
    {
        await using var scope = _host.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<Downloads.Application.DownloadService>()
            .ApplyStatusAsync(snapshot.InfoHash, snapshot);
    }

    /// <summary>
    /// Leaves the import hand-off claimed by a process that no longer exists. Written as raw SQL
    /// because no legitimate code path produces this row — only a process being killed does.
    /// </summary>
    private async Task StrandTheImportCommandAsync()
    {
        await using var scope = _host.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
        var stranded = await dbContext.Database.ExecuteSqlAsync(
            $"UPDATE operations.command SET state = 'Running' WHERE state = 'Queued'");
        Assert.True(stranded > 0, "there was no queued command to strand");
    }

    /// <summary>Marks every delivered outbox row undelivered, so the relay hands them all over again.</summary>
    private async Task ReplayEveryPublishedEventAsync()
    {
        await using var scope = _host.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
        await dbContext.Outbox
            .Where(message => message.Published)
            .ExecuteUpdateAsync(setters => setters.SetProperty(message => message.Published, false));
    }

    private async Task RestartAsync()
    {
        _host = await RecoveryHost.RestartAsync(_host, Database, _fakes);
        _driver = new RecoveryDriver(_host);
        await _driver.DrainAsync();
    }
}
