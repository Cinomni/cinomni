using Cinomni.Acquisition.Contracts;
using Cinomni.Downloads.Messaging;
using Cinomni.Kernel.Identifiers;
using Cinomni.Operations.Persistence;
using Cinomni.Recovery.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Recovery.Tests;

/// <summary>
/// A release Acquisition handed off that never becomes a download: every attempt to give it to the
/// engine fails. No task exists, so nothing downstream can report the failure — and a goal waiting in
/// <c>Downloading</c> for a task that is never coming has no timeout and no failure to move it.
/// </summary>
public sealed class DownloadNeverStartsScenarioTests : IAsyncLifetime
{
    private const string Database = "cinomni_test_recovery_never_starts";
    private const string DownloadUrl = "magnet:?xt=urn:btih:never-starts";

    private readonly RecoveryFakes _fakes = new();
    private ServiceProvider _host = null!;
    private RecoveryDriver _driver = null!;
    private Guid _workId;

    public async Task InitializeAsync()
    {
        _workId = _fakes.Catalog.SeedMovie("Never Starts", 2021);
        _fakes.Engine.ScriptRelease(DownloadUrl, "Never.Starts.2021.1080p.WEB-DL");
        _host = await RecoveryHost.CreateAsync(Database, _fakes);
        _driver = new RecoveryDriver(_host);
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task A_release_the_engine_never_accepts_sends_the_goal_back_to_searching()
    {
        _fakes.Engine.RefuseAdds = true;
        var targetId = Uuid7.New();
        await _driver.AcquireAsync(targetId, _workId, "release-dead", DownloadUrl, [_workId]);

        await SpendTheAddDownloadAttemptsAsync();

        var add = Assert.Single(await _driver.CommandsAsync(DownloadCommandNames.AddDownload));
        Assert.Equal(CommandState.Failed, add.State);
        Assert.Empty(await _driver.DownloadTasksAsync());

        // The goal is not lost and not left waiting: it goes back to looking for another release.
        Assert.Equal(IntentState.Searching, await _driver.IntentStateAsync(targetId));
    }

    [Fact]
    public async Task A_late_report_about_an_earlier_attempt_does_not_fail_the_current_one()
    {
        var targetId = Uuid7.New();
        var (intentId, attemptId) = await _driver.AcquireAsync(targetId, _workId, "release-live", DownloadUrl, [_workId]);
        Assert.Equal(IntentState.Downloading, await _driver.IntentStateAsync(targetId));

        await using (var scope = _host.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IAcquisitionCommands>()
                .MarkDownloadNotStartedAsync(intentId, Guid.NewGuid(), "about some other attempt");
        }

        Assert.Equal(IntentState.Downloading, await _driver.IntentStateAsync(targetId));

        // The attempt it does name is failed like any download.
        await using (var scope = _host.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IAcquisitionCommands>()
                .MarkDownloadNotStartedAsync(intentId, attemptId, "never started");
        }

        Assert.Equal(IntentState.Searching, await _driver.IntentStateAsync(targetId));
    }

    /// <summary>
    /// Brings the add to its last attempt now instead of after its backoff, then lets it run: the
    /// spacing between attempts is the queue's business and not what is under test.
    /// </summary>
    private async Task SpendTheAddDownloadAttemptsAsync()
    {
        await using (var scope = _host.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<OperationsDbContext>().Database.ExecuteSqlAsync(
                $"UPDATE operations.command SET attempts = max_attempts - 1, run_after = NULL WHERE command_type = {DownloadCommandNames.AddDownload}");
        }

        await _driver.DrainAsync();
    }
}
