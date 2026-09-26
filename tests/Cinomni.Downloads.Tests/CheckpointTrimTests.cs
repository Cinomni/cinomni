using Cinomni.Downloads.Contracts;
using Cinomni.Downloads.Messaging;
using Cinomni.Downloads.Persistence;
using Cinomni.Kernel.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cinomni.Downloads.Tests;

/// <summary>
/// Integration tests for the checkpoint trim against a real PostgreSQL instance. Downloads deletes
/// nothing: the task and its history are the acquisition audit trail. Only the resume blob of a
/// download that finished long ago is released, and a seeding torrent keeps its checkpoint at any age
/// because that is what lets it resume seeding after a restart without re-hashing.
/// </summary>
public sealed class CheckpointTrimTests : IAsyncLifetime
{
    private static readonly TimeSpan Window = TimeSpan.FromDays(30);
    private static readonly byte[] Checkpoint = [1, 2, 3, 4, 5, 6, 7, 8];

    private readonly FakeTorrentEngine _engine = new();
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync() =>
        _provider = await DownloadsTestHost.CreateAsync("cinomni_test_downloads_retention", _engine);

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task Only_a_finished_download_loses_its_checkpoint_and_the_row_survives()
    {
        var completed = await SeedAsync(task => task.MarkCompleted("/data/done", DateTimeOffset.UtcNow));
        var failed = await SeedAsync(task => task.Fail("engine gave up", DateTimeOffset.UtcNow));
        var seeding = await SeedAsync(task =>
        {
            task.MarkCompleted("/data/done", DateTimeOffset.UtcNow);
            task.MarkSeeding(DateTimeOffset.UtcNow);
        });
        var active = await SeedAsync(task => task.MarkDownloading(DateTimeOffset.UtcNow));

        await AgeAsync(DateTimeOffset.UtcNow - Window - TimeSpan.FromDays(1));

        await TrimAsync();

        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DownloadsDbContext>();

        // Nothing is deleted, ever: four tasks in, four tasks out, with their history.
        Assert.Equal(4, await dbContext.Tasks.CountAsync());
        Assert.True(await dbContext.History.AnyAsync(h => h.DownloadTaskId == completed));

        Assert.Null(await ResumeDataAsync(dbContext, completed));
        Assert.Null(await ResumeDataAsync(dbContext, failed));

        // A seeding torrent is still live in the sidecar; an active one obviously is.
        Assert.NotNull(await ResumeDataAsync(dbContext, seeding));
        Assert.NotNull(await ResumeDataAsync(dbContext, active));
    }

    [Fact]
    public async Task A_recent_finished_download_keeps_its_checkpoint_and_a_second_run_is_a_no_op()
    {
        var recent = await SeedAsync(task => task.MarkCompleted("/data/done", DateTimeOffset.UtcNow));

        await TrimAsync();

        await using (var scope = _provider.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<DownloadsDbContext>();
            Assert.NotNull(await ResumeDataAsync(dbContext, recent));
        }

        await AgeAsync(DateTimeOffset.UtcNow - Window - TimeSpan.FromDays(3));
        await TrimAsync();
        await TrimAsync();

        await using (var scope = _provider.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<DownloadsDbContext>();
            Assert.Null(await ResumeDataAsync(dbContext, recent));
            Assert.Equal(1, await dbContext.Tasks.CountAsync());
        }
    }

    private async Task TrimAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<TrimCheckpointsCommand>>();
        Assert.True((await handler.HandleAsync(new TrimCheckpointsCommand())).IsSuccess);
    }

    private async Task AgeAsync(DateTimeOffset updatedAt)
    {
        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DownloadsDbContext>();
        await dbContext.Tasks.ExecuteUpdateAsync(t => t.SetProperty(x => x.UpdatedAt, updatedAt));
    }

    private static Task<byte[]?> ResumeDataAsync(DownloadsDbContext dbContext, Guid taskId) =>
        dbContext.Tasks.AsNoTracking().Where(t => t.Id == taskId).Select(t => t.ResumeData).SingleAsync();

    private async Task<Guid> SeedAsync(Action<DownloadTask> advance)
    {
        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DownloadsDbContext>();

        var now = DateTimeOffset.UtcNow;
        var task = DownloadTask.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "release-guid", "magnet:?xt=urn:btih:abcdef", "/data/test-downloads",
            new SeedingPolicy(RatioLimit: 1.0, SeedTimeLimitSeconds: null), now);

        advance(task);
        task.SaveCheckpoint(Checkpoint, now);

        dbContext.Tasks.Add(task);
        await dbContext.SaveChangesAsync();
        return task.Id;
    }
}
