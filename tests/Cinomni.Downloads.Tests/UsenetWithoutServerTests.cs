using Cinomni.Discovery.Contracts;
using Cinomni.Downloads.Application;
using Cinomni.Downloads.Contracts;
using Cinomni.Downloads.Engine;
using Cinomni.Downloads.Persistence;
using Cinomni.Downloads.Usenet;
using Cinomni.Kernel.Identifiers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Downloads.Tests;

/// <summary>
/// A Usenet release on an installation that has no Usenet server set, which is every installation
/// that has not opted in. The release fails with that reason, is never handed to the torrent engine,
/// and its NZB is not taken from the indexer: an indexer may count every NZB fetched against a daily
/// limit, and without a server nothing fetched could be used.
/// </summary>
public sealed class UsenetWithoutServerTests : IAsyncLifetime
{
    private readonly DirectoryInfo _staging = Directory.CreateTempSubdirectory("cinomni-usenet-none-");
    private readonly FakeTorrentEngine _engine = new();
    private readonly CountingReleaseFiles _indexer = new();
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync() =>
        _provider = await DownloadsTestHost.CreateAsync(
            "cinomni_test_usenetnoserver",
            _engine,
            services =>
            {
                // No UsenetOptions here: the module's own default, an empty host, is what is under test.
                services.AddSingleton(new SidecarOptions { StagingPath = _staging.FullName.Replace('\\', '/') });
                services.AddSingleton<IReleaseFileSource>(_indexer);
            });

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        _staging.Delete(recursive: true);
    }

    [Fact]
    public async Task A_usenet_release_fails_with_the_no_server_reason()
    {
        var attemptId = await QueueUsenetReleaseAsync();

        await TickAsync();

        var task = await GetTaskAsync(attemptId);
        Assert.Equal(DownloadState.Error, task.State);
        Assert.Equal(UsenetDownloadService.NoServer, task.LastError);
        Assert.Equal(UsenetDownloadService.NoServer, Assert.Single(task.History, entry => entry.Trigger == "Failed").Note);
    }

    [Fact]
    public async Task A_usenet_release_is_not_handed_to_the_torrent_engine()
    {
        await QueueUsenetReleaseAsync();

        await TickAsync();

        Assert.Empty(_engine.Adds);
    }

    [Fact]
    public async Task The_nzb_is_not_taken_from_the_indexer_and_nothing_is_written()
    {
        var attemptId = await QueueUsenetReleaseAsync();

        await TickAsync();

        Assert.Empty(_indexer.Asked);
        Assert.False(Directory.Exists(Path.Combine(_staging.FullName, attemptId.ToString())));
    }

    private async Task<Guid> QueueUsenetReleaseAsync()
    {
        var attemptId = Uuid7.New();
        await using var scope = _provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DownloadService>().AddDownloadAsync(
            attemptId: attemptId,
            intentId: Uuid7.New(),
            workId: Uuid7.New(),
            targetId: Uuid7.New(),
            releaseGuid: "n1",
            downloadUrl: "https://indexer.example/nzb/n1",
            protocol: DownloadProtocols.Usenet);
        return attemptId;
    }

    private async Task TickAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<UsenetDownloadService>().AdvanceAsync(CancellationToken.None);
    }

    private async Task<DownloadTask> GetTaskAsync(Guid attemptId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DownloadsDbContext>();
        return await dbContext.Tasks
            .AsNoTracking()
            .Include(t => t.History)
            .SingleAsync(t => t.AttemptId == attemptId);
    }

    /// <summary>An indexer that would hand over a video NZB, and records every time it is asked.</summary>
    private sealed class CountingReleaseFiles : IReleaseFileSource
    {
        private static readonly byte[] Nzb =
            "<nzb><file subject=\"&quot;Film.mkv&quot; yEnc (1/1)\"><segments><segment bytes=\"9\" number=\"1\">s1@example</segment></segments></file></nzb>"u8.ToArray();

        public List<string> Asked { get; } = [];

        public Task<ReleaseFile> FetchAsync(string downloadUrl, CancellationToken cancellationToken = default)
        {
            Asked.Add(downloadUrl);
            return Task.FromResult(new ReleaseFile(ReleaseFileOutcome.TorrentFile, Nzb));
        }
    }
}
