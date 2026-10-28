using System.Globalization;
using System.Text;
using Cinomni.Downloads.Application;
using Cinomni.Downloads.Contracts;
using Cinomni.Downloads.Engine;
using Cinomni.Downloads.Persistence;
using Cinomni.Downloads.Usenet;
using Cinomni.Kernel.Identifiers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Cinomni.Downloads.Tests.YencWire;

namespace Cinomni.Downloads.Tests;

/// <summary>
/// One Usenet download advanced tick by tick, against a real PostgreSQL instance and a loopback NNTP
/// server. What is asserted is what the operator sees: a download that a bad minute on the server does
/// not kill, one that a missing article does, and a staging folder that holds the file and nothing else.
/// </summary>
public sealed class UsenetDownloadServiceTests : IAsyncLifetime
{
    private readonly DirectoryInfo _staging = Directory.CreateTempSubdirectory("cinomni-usenet-");
    private readonly FakeNntpServer _server = new();
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync() =>
        _provider = await DownloadsTestHost.CreateAsync(
            "cinomni_test_usenetdownload",
            new FakeTorrentEngine(),
            services =>
            {
                services.AddSingleton(new SidecarOptions { StagingPath = _staging.FullName.Replace('\\', '/') });
                services.AddSingleton(_server.Options);
            });

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _server.DisposeAsync();
        _staging.Delete(recursive: true);
    }

    [Fact]
    public async Task A_finished_download_is_one_file_and_keeps_no_parts()
    {
        var first = AllByteValues();
        var second = "the rest of the film"u8.ToArray();
        var download = await ArrangeAsync(first, second);

        await TickAsync();

        Assert.Equal(DownloadState.Completed, (await GetTaskAsync(download.AttemptId)).State);
        byte[] film = [.. first, .. second];
        Assert.Equal(film, await File.ReadAllBytesAsync(Path.Combine(download.Folder, "Film.mkv")));
        // The parts are as large as the film. Left behind they double what the download takes on disk.
        Assert.False(Directory.Exists(Path.Combine(download.Folder, ".parts")));
    }

    [Fact]
    public async Task A_server_that_drops_the_connection_does_not_fail_the_download()
    {
        _server.Behavior = connection => connection == 0 ? ConnectionBehavior.Drop : ConnectionBehavior.Serve;
        var download = await ArrangeAsync("only part"u8.ToArray());

        await TickAsync();

        var task = await GetTaskAsync(download.AttemptId);
        Assert.Equal(DownloadState.Downloading, task.State);
        Assert.DoesNotContain(task.History, entry => entry.Trigger == "Failed");

        await TickAsync();

        Assert.Equal(DownloadState.Completed, (await GetTaskAsync(download.AttemptId)).State);
    }

    [Fact]
    public async Task A_server_that_stays_down_fails_the_download_once_the_grace_has_passed()
    {
        _server.Behavior = _ => ConnectionBehavior.Drop;
        var download = await ArrangeAsync("only part"u8.ToArray());

        await TickAsync();
        Assert.Equal(DownloadState.Downloading, (await GetTaskAsync(download.AttemptId)).State);

        // The first failure is on record, so the grace is measured from it. An hour on is past any grace.
        BackdateOutage(download, TimeSpan.FromHours(1));
        await TickAsync();

        var task = await GetTaskAsync(download.AttemptId);
        Assert.Equal(DownloadState.Error, task.State);
        Assert.Contains("did not answer", task.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_outage_that_ends_is_forgotten()
    {
        _server.Behavior = connection => connection == 0 ? ConnectionBehavior.Drop : ConnectionBehavior.Serve;
        var download = await ArrangeAsync("only part"u8.ToArray());

        await TickAsync();
        BackdateOutage(download, TimeSpan.FromHours(1));
        await TickAsync();

        // The server is back and the download goes through. An old outage must not count against it.
        Assert.Equal(DownloadState.Completed, (await GetTaskAsync(download.AttemptId)).State);
        Assert.False(File.Exists(OutageMarker(download)));
    }

    [Fact]
    public async Task A_download_resumes_after_the_server_drops_it_part_way()
    {
        _server.Behavior = connection => connection == 0 ? ConnectionBehavior.ServeOneThenDrop : ConnectionBehavior.Serve;
        var download = await ArrangeAsync("first part"u8.ToArray(), "second part"u8.ToArray());

        await TickAsync();

        // One article got through before the drop, so this tick moved the download on: not an outage.
        Assert.Equal(DownloadState.Downloading, (await GetTaskAsync(download.AttemptId)).State);
        Assert.False(File.Exists(OutageMarker(download)));

        await TickAsync();

        Assert.Equal(DownloadState.Completed, (await GetTaskAsync(download.AttemptId)).State);
        Assert.Equal(2, _server.Connections);
    }

    [Fact]
    public async Task A_missing_article_fails_the_download_at_once()
    {
        // No par2 repair is built, so an article the server does not have is a release that cannot be
        // finished, and waiting does not change that.
        var download = await ArrangeAsync("only part"u8.ToArray(), missing: [1]);

        await TickAsync();

        Assert.Equal(DownloadState.Error, (await GetTaskAsync(download.AttemptId)).State);
    }

    [Fact]
    public async Task A_failed_download_does_not_keep_its_parts()
    {
        var download = await ArrangeAsync("first part"u8.ToArray(), "second part"u8.ToArray(), missing: [2]);

        await TickAsync();

        Assert.Equal(DownloadState.Error, (await GetTaskAsync(download.AttemptId)).State);
        Assert.False(Directory.Exists(Path.Combine(download.Folder, ".parts")));
    }

    private sealed record Arranged(Guid AttemptId, string Folder);

    /// <summary>
    /// A Usenet task for a one-file release whose articles carry <paramref name="parts"/>, in order. The
    /// release's NZB is already in the task's folder, as it is after the first tick, so the test does not
    /// need an indexer. Segments numbered in <paramref name="missing"/> are in the NZB but not on the server.
    /// </summary>
    private async Task<Arranged> ArrangeAsync(byte[] first, byte[]? second = null, int[]? missing = null)
    {
        byte[][] parts = second is null ? [first] : [first, second];
        var segments = new StringBuilder();
        for (var i = 0; i < parts.Length; i++)
        {
            var number = i + 1;
            segments.Append(CultureInfo.InvariantCulture, $"<segment bytes=\"{parts[i].Length}\" number=\"{number}\">s{number}@example</segment>");
            if (missing is null || !missing.Contains(number))
            {
                _server.Add($"s{number}@example", SignedArticle(parts[i]));
            }
        }

        var attemptId = Uuid7.New();
        await using (var scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<DownloadService>().AddDownloadAsync(
                attemptId: attemptId,
                intentId: Uuid7.New(),
                workId: Uuid7.New(),
                targetId: Uuid7.New(),
                releaseGuid: "n1",
                downloadUrl: "https://indexer.example/nzb/n1",
                protocol: DownloadProtocols.Usenet);
        }

        var folder = Path.Combine(_staging.FullName, attemptId.ToString());
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(
            Path.Combine(folder, "release.nzb"),
            $"<nzb><file subject=\"&quot;Film.mkv&quot; yEnc (1/{parts.Length})\"><segments>{segments}</segments></file></nzb>");
        return new Arranged(attemptId, folder);
    }

    private async Task TickAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<UsenetDownloadService>().AdvanceAsync(CancellationToken.None);
    }

    private static string OutageMarker(Arranged download) => Path.Combine(download.Folder, UsenetDownloadService.OutageMarker);

    private static void BackdateOutage(Arranged download, TimeSpan age) =>
        File.WriteAllText(OutageMarker(download), (DateTimeOffset.UtcNow - age).ToString("O", CultureInfo.InvariantCulture));

    private async Task<DownloadTask> GetTaskAsync(Guid attemptId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DownloadsDbContext>();
        return await dbContext.Tasks
            .AsNoTracking()
            .Include(t => t.History)
            .SingleAsync(t => t.AttemptId == attemptId);
    }
}
