using Cinomni.Downloads.Contracts;
using Cinomni.Downloads.Engine;
using Cinomni.Downloads.Persistence;
using Cinomni.Kernel.Identifiers;
using Cinomni.Recovery.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Recovery.Tests;

/// <summary>
/// Validation scenario 14, the magnet case — the process restarts before the torrent's metadata has
/// resolved.
/// <para>
/// A magnet is a reference, not a torrent: the engine has an info-hash and nothing else until the
/// swarm hands over the info dictionary, so the platform has no name and no file list for it. The
/// checkpoint written afterwards carries that dictionary, which means the layout can become known on
/// the very re-add a restart performs — and that is the only moment the platform can capture it.
/// After the re-add the task has a name, and the ordinary status path only fetches a layout while it
/// has none.
/// </para>
/// <para>
/// Miss it and <c>downloads.torrent_files</c> stays empty for the life of the task: the completion
/// carries no layout to Import, per-file priorities have nothing to act on, and nothing ever tells
/// the interface the metadata arrived.
/// </para>
/// </summary>
[Trait("Scenario", "14")]
public sealed class RestartWhileMetadataResolvesTests : IAsyncLifetime
{
    private const string Database = "cinomni_test_recovery_metadata";
    private const string ReleaseGuid = "release-magnet";
    private const string DownloadUrl = "magnet:?xt=urn:btih:unresolved";
    private const string TorrentName = "Resolved.Feature.2025.1080p.WEB-DL";

    private readonly RecoveryFakes _fakes = new();
    private ServiceProvider _host = null!;
    private RecoveryDriver _driver = null!;

    private Guid _workId;
    private Guid _targetId;
    private Guid _attemptId;

    public async Task InitializeAsync()
    {
        _workId = _fakes.Catalog.SeedMovie("Resolved Feature", 2025);
        _targetId = Uuid7.New();

        // Deliberately not scripted: the engine took the magnet and cannot yet say what it is.
        _host = await RecoveryHost.CreateAsync(Database, _fakes);
        _driver = new RecoveryDriver(_host);

        (_, _attemptId) = await _driver.AcquireAsync(_targetId, _workId, ReleaseGuid, DownloadUrl, [_workId]);
        await _driver.ApplyStatusAsync(_fakes.Engine.SnapshotFor(DownloadUrl, "downloading_metadata"));
        await _driver.SaveCheckpointsAsync();
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task A_layout_that_resolved_while_the_process_was_away_is_recorded_and_announced()
    {
        var before = await _driver.DownloadTaskAsync(_attemptId);
        Assert.Equal(string.Empty, before!.Name);
        Assert.Empty(_fakes.Events.MetadataReady);

        // The sidecar went with the backend and comes back holding nothing; the checkpoint it is
        // handed carries the info dictionary, so this time it can answer with the name and the files.
        _fakes.Engine.ForgetEverything();
        _fakes.Engine.ResolveMetadata(
            DownloadUrl,
            TorrentName,
            new TorrentFileInfo(0, $"{TorrentName}/{TorrentName}.mkv", 4000, FilePriorityLevel.Normal),
            new TorrentFileInfo(1, $"{TorrentName}/sample.mkv", 60, FilePriorityLevel.Normal));

        await RestartAsync();

        var after = await _driver.DownloadTaskAsync(_attemptId);
        Assert.Equal(TorrentName, after!.Name);

        var files = await FilesAsync(after.Id);
        Assert.Equal(2, files.Count);
        Assert.Equal($"{TorrentName}/{TorrentName}.mkv", files[0].Path);

        var announced = Assert.Single(_fakes.Events.MetadataReady);
        Assert.Equal(TorrentName, announced.Name);
        Assert.Equal(after.Id, announced.DownloadTaskId);
    }

    [Fact]
    public async Task The_completion_that_follows_carries_the_layout_the_restart_captured()
    {
        _fakes.Engine.ForgetEverything();
        _fakes.Engine.ResolveMetadata(
            DownloadUrl,
            TorrentName,
            new TorrentFileInfo(0, $"{TorrentName}/{TorrentName}.mkv", 4000, FilePriorityLevel.Normal));

        await RestartAsync();
        await _driver.ApplyStatusAsync(_fakes.Engine.SnapshotFor(DownloadUrl, "finished", isFinished: true));

        // This is what the missing layout costs downstream: Import is handed the download's files with
        // the completion, and an empty list is indistinguishable from a torrent with no files at all.
        var completed = Assert.Single(_fakes.Events.DownloadCompleted);
        var file = Assert.Single(completed.Files ?? []);
        Assert.Equal($"{TorrentName}/{TorrentName}.mkv", file.RelativePath);
    }

    [Fact]
    public async Task A_restart_that_still_cannot_name_the_torrent_announces_nothing()
    {
        // The metadata has not resolved and the re-add cannot make it: an announcement here would be
        // saying the layout is known when the platform has never seen one.
        _fakes.Engine.ForgetEverything();

        await RestartAsync();

        var after = await _driver.DownloadTaskAsync(_attemptId);
        Assert.Equal(string.Empty, after!.Name);
        Assert.Empty(_fakes.Events.MetadataReady);
        Assert.Empty(await FilesAsync(after.Id));
        Assert.Contains(after.History, line => line.Trigger == "Recover");
    }

    private async Task<List<TorrentFile>> FilesAsync(Guid taskId)
    {
        await using var scope = _host.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<DownloadsDbContext>()
            .Files.AsNoTracking().Where(f => f.DownloadTaskId == taskId).OrderBy(f => f.Index).ToListAsync();
    }

    private async Task RestartAsync()
    {
        _host = await RecoveryHost.RestartAsync(_host, Database, _fakes);
        _driver = new RecoveryDriver(_host);
        await _driver.DrainAsync();
    }
}
