using Cinomni.Kernel.Messaging;
using Cinomni.Metadata.Application;
using Cinomni.Metadata.Contracts;
using Cinomni.Metadata.Messaging;
using Cinomni.Metadata.Persistence;
using Cinomni.Operations.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cinomni.Metadata.Tests;

/// <summary>
/// Integration tests for the snapshot retention sweep against a real PostgreSQL instance. Every
/// successful refresh appends a whole new snapshot tree, so the superseded ones are the module's
/// fastest-growing data — but the snapshot <c>refresh_states</c> currently points at is what the rest
/// of the installation resolves artwork through, and it must survive at any age.
/// <para>
/// The snapshots here are produced by the real refresh path rather than hand-built, so the guard is
/// tested against the shape the module actually writes.
/// </para>
/// </summary>
public sealed class SnapshotRetentionTests : IAsyncLifetime
{
    private const string Provider = "tvdb";
    private static readonly TimeSpan Window = TimeSpan.FromDays(30);

    private readonly FakeMetadataSource _source = FakeMetadataSource.ForSeries(Provider);
    private ServiceProvider _host = null!;

    public async Task InitializeAsync() =>
        _host = await MetadataTestHost.CreateAsync(
            "cinomni_test_metadata_retention",
            [_source],
            options =>
            {
                options.Providers = [Provider];
                options.SnapshotRetention = Window;
                // Zero TTL so the second refresh really produces a second snapshot.
                options.SeriesRefreshTtl = TimeSpan.Zero;
                options.RefreshTtl = TimeSpan.Zero;
            });

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task A_superseded_snapshot_is_removed_with_its_children_and_the_current_one_survives()
    {
        var workId = Guid.NewGuid();

        await RefreshAsync(workId);
        var superseded = await CurrentSnapshotIdAsync(workId);

        await RefreshAsync(workId);
        var current = await CurrentSnapshotIdAsync(workId);
        Assert.NotEqual(superseded, current);

        // Age both snapshots well past the window: only reachability decides which one stays.
        await AgeAllSnapshotsAsync(DateTimeOffset.UtcNow - Window - TimeSpan.FromDays(60));

        await PurgeAsync();

        await using var scope = _host.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MetadataDbContext>();

        Assert.False(await dbContext.Snapshots.AnyAsync(s => s.Id == superseded));
        Assert.False(await dbContext.Seasons.AnyAsync(s => s.SnapshotId == superseded));
        Assert.False(await dbContext.Episodes.AnyAsync(e => e.SnapshotId == superseded));
        Assert.False(await dbContext.Artwork.AnyAsync(a => a.SnapshotId == superseded));

        Assert.True(await dbContext.Snapshots.AnyAsync(s => s.Id == current));
        Assert.Equal(2, await dbContext.Seasons.CountAsync(s => s.SnapshotId == current));
        Assert.Equal(4, await dbContext.Episodes.CountAsync(e => e.SnapshotId == current));

        // The invariant the rest of the installation depends on: the retained snapshot is still the
        // one every consumer was handed, and it is still readable.
        var query = scope.ServiceProvider.GetRequiredService<IMetadataQuery>();
        var readBack = await query.GetSnapshotAsync(new MetadataSnapshotId(current));
        Assert.NotNull(readBack);
        Assert.Equal(FakeMetadataSource.PosterEnUrl, readBack.PosterUrl);

        var structure = await query.GetSeriesStructureAsync(new MetadataSnapshotId(current));
        Assert.NotNull(structure);
        Assert.Equal(4, structure.Episodes.Count);
    }

    [Fact]
    public async Task A_recent_superseded_snapshot_survives_and_a_second_run_removes_nothing()
    {
        var workId = Guid.NewGuid();

        await RefreshAsync(workId);
        var superseded = await CurrentSnapshotIdAsync(workId);
        await RefreshAsync(workId);

        // Inside the window: nothing is eligible yet.
        await PurgeAsync();

        await using (var scope = _host.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<MetadataDbContext>();
            Assert.True(await dbContext.Snapshots.AnyAsync(s => s.Id == superseded));
        }

        await AgeAllSnapshotsAsync(DateTimeOffset.UtcNow - Window - TimeSpan.FromDays(1));
        await PurgeAsync();
        await PurgeAsync();

        await using (var scope = _host.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<MetadataDbContext>();
            Assert.Equal(1, await dbContext.Snapshots.CountAsync(s => s.WorkId == workId));
        }
    }

    private async Task PurgeAsync()
    {
        await using var scope = _host.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<PurgeSnapshotsCommand>>();
        Assert.True((await handler.HandleAsync(new PurgeSnapshotsCommand())).IsSuccess);
    }

    private async Task<Guid> CurrentSnapshotIdAsync(Guid workId)
    {
        await using var scope = _host.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MetadataDbContext>();
        var state = await dbContext.RefreshStates
            .AsNoTracking()
            .SingleAsync(s => s.WorkId == workId && s.Provider == Provider);
        Assert.NotNull(state.SnapshotId);
        return state.SnapshotId.Value;
    }

    private async Task AgeAllSnapshotsAsync(DateTimeOffset fetchedAt)
    {
        await using var scope = _host.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MetadataDbContext>();
        await dbContext.Snapshots.ExecuteUpdateAsync(s => s.SetProperty(x => x.FetchedAt, fetchedAt));
    }

    private async Task RefreshAsync(Guid workId)
    {
        await using (var scope = _host.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ICommandQueue>().EnqueueAsync(
                new RefreshMetadataCommand(
                    workId, Provider, FakeMetadataSource.SeriesExternalId, MetadataMediaKind.Series),
                idempotencyKey: $"test-retention-refresh:{workId}:{Guid.NewGuid()}");
        }

        while (true)
        {
            var commands = await DrainCommandsAsync();
            var events = await DrainOutboxAsync();
            if (commands == 0 && events == 0)
            {
                return;
            }
        }
    }

    private async Task<int> DrainCommandsAsync()
    {
        await using var scope = _host.CreateAsyncScope();
        var processor = scope.ServiceProvider.GetRequiredService<CommandProcessor>();
        var total = 0;
        int processed;
        while ((processed = await processor.ProcessBatchAsync()) > 0)
        {
            total += processed;
        }

        return total;
    }

    private async Task<int> DrainOutboxAsync()
    {
        await using var scope = _host.CreateAsyncScope();
        var relay = scope.ServiceProvider.GetRequiredService<OutboxRelay>();
        var total = 0;
        int published;
        while ((published = await relay.ProcessBatchAsync()) > 0)
        {
            total += published;
        }

        return total;
    }
}
