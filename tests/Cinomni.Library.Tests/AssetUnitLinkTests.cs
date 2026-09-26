using Cinomni.Import.Contracts;
using Cinomni.Kernel.Identifiers;
using Cinomni.Kernel.Messaging;
using Cinomni.Library.Contracts;
using Cinomni.Operations.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Library.Tests;

/// <summary>
/// Integration tests for the asset↔unit relation against a real PostgreSQL instance. It is the
/// semantically correct N:M link ("which file plays episode X"), and it is <b>additive</b>:
/// <c>asset_target_links</c> keeps holding the acquiring monitored target exactly as it shipped.
/// </summary>
public sealed class AssetUnitLinkTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync() =>
        _provider = await LibraryTestHost.CreateAsync("cinomni_test_library_units");

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task An_episode_asset_links_to_its_unit()
    {
        var workId = Uuid7.New();
        var episodeId = Uuid7.New();
        var assetId = Uuid7.New();

        await RegisterAsync(assetId, workId, targetIds: [], unitIds: [episodeId], path: "/lib/The Wire/Season 01/S01E01.mkv");

        var detail = await QueryAsync(q => q.GetAsync(new MediaAssetId(assetId)));
        Assert.Equal(episodeId, Assert.Single(detail!.UnitIds!));
        Assert.Empty(detail.TargetIds);
    }

    [Fact]
    public async Task A_multi_episode_file_links_to_two_units()
    {
        var workId = Uuid7.New();
        var first = Uuid7.New();
        var second = Uuid7.New();
        var assetId = Uuid7.New();

        // One physical file, two episodes: ux_media_versions_full_path makes two assets impossible.
        await RegisterAsync(assetId, workId, [], [first, second], "/lib/Show/Season 01/S01E01-E02.mkv");

        var detail = await QueryAsync(q => q.GetAsync(new MediaAssetId(assetId)));
        Assert.Equal(2, detail!.UnitIds!.Count);
        Assert.Contains(first, detail.UnitIds!);
        Assert.Contains(second, detail.UnitIds!);
        Assert.Single(detail.Versions);
    }

    [Fact]
    public async Task Get_by_unit_returns_the_playable_asset()
    {
        var workId = Uuid7.New();
        var firstEpisode = Uuid7.New();
        var secondEpisode = Uuid7.New();
        var firstAsset = Uuid7.New();
        var secondAsset = Uuid7.New();

        await RegisterAsync(firstAsset, workId, [], [firstEpisode], "/lib/Show/Season 01/S01E01.mkv");
        await RegisterAsync(secondAsset, workId, [], [secondEpisode], "/lib/Show/Season 01/S01E02.mkv");

        // GetByWorkAsync returns both with nothing to tell them apart; the unit query is the answer.
        Assert.Equal(2, (await QueryAsync(q => q.GetByWorkAsync(workId))).Count);
        var byUnit = await QueryAsync(q => q.GetByUnitAsync(secondEpisode));
        Assert.Equal(secondAsset, Assert.Single(byUnit).Id.Value);

        var bothUnits = await QueryAsync(q => q.GetByUnitsAsync([firstEpisode, secondEpisode]));
        Assert.Equal(2, bothUnits.Count);
        Assert.Empty(await QueryAsync(q => q.GetByUnitsAsync([])));
    }

    [Fact]
    public async Task A_second_registration_adds_a_missing_link_instead_of_short_circuiting()
    {
        var workId = Uuid7.New();
        var first = Uuid7.New();
        var second = Uuid7.New();
        var targetId = Uuid7.New();
        var assetId = Uuid7.New();
        const string path = "/lib/Show/Season 01/S01E01-E02.mkv";

        await RegisterAsync(assetId, workId, [], [first], path);
        // The redelivery carries a link the stored asset does not hold yet. A bare early return here
        // would drop the second episode for good.
        await RegisterAsync(assetId, workId, [targetId], [first, second], path);

        var detail = await QueryAsync(q => q.GetAsync(new MediaAssetId(assetId)));
        Assert.Equal(2, detail!.UnitIds!.Count);
        Assert.Equal(targetId, Assert.Single(detail.TargetIds));
        Assert.Single(detail.Versions); // still exactly one asset and one version
    }

    [Fact]
    public async Task Linking_the_same_unit_twice_is_idempotent()
    {
        var workId = Uuid7.New();
        var episodeId = Uuid7.New();
        var assetId = Uuid7.New();
        await RegisterAsync(assetId, workId, [], [episodeId], "/lib/Show/Season 01/S01E01.mkv");

        await LinkAsync(assetId, [episodeId, episodeId]);
        await LinkAsync(assetId, [episodeId]);
        await LinkAsync(assetId, []);
        await LinkAsync(Uuid7.New(), [episodeId]); // unknown asset → no-op, no throw

        var detail = await QueryAsync(q => q.GetAsync(new MediaAssetId(assetId)));
        Assert.Equal(episodeId, Assert.Single(detail!.UnitIds!));
    }

    [Fact]
    public async Task Linking_a_unit_discovered_after_registration_adds_it()
    {
        var workId = Uuid7.New();
        var assetId = Uuid7.New();
        var lateUnit = Uuid7.New();
        await RegisterAsync(assetId, workId, [], [], "/lib/Show/Season 01/S01E01.mkv");

        await LinkAsync(assetId, [lateUnit]);

        var detail = await QueryAsync(q => q.GetAsync(new MediaAssetId(assetId)));
        Assert.Equal(lateUnit, Assert.Single(detail!.UnitIds!));
    }

    [Fact]
    public async Task A_movie_asset_still_links_to_its_work_unit()
    {
        var workId = Uuid7.New();
        var targetId = Uuid7.New();
        var assetId = Uuid7.New();

        // The movie path: the unit IS the work, and the acquiring target link is unchanged.
        await DeliverMediaAvailableAsync(new MediaAvailable(
            assetId, workId, [targetId], Uuid7.New(), Uuid7.New(), "/lib/Movie/Movie.mkv", 2000, MediaInfo.Empty,
            UnitIds: [workId]));
        await DrainAsync();

        var detail = await QueryAsync(q => q.GetAsync(new MediaAssetId(assetId)));
        Assert.Equal(workId, Assert.Single(detail!.UnitIds!));
        Assert.Equal(targetId, Assert.Single(detail.TargetIds));
    }

    [Fact]
    public async Task The_registered_event_carries_the_unit_ids()
    {
        var workId = Uuid7.New();
        var episodeId = Uuid7.New();
        var assetId = Uuid7.New();

        await RegisterAsync(assetId, workId, [], [episodeId], "/lib/Show/Season 01/S01E01.mkv");

        // Consumers must be able to act per episode without reaching back into Library.
        var summary = Assert.Single(await QueryAsync(q => q.GetByUnitAsync(episodeId)));
        Assert.Equal(episodeId, Assert.Single(summary.UnitIds!));
    }

    // -- helpers ---------------------------------------------------------------------------------

    private async Task RegisterAsync(
        Guid assetId,
        Guid workId,
        IReadOnlyList<Guid> targetIds,
        IReadOnlyList<Guid> unitIds,
        string path)
    {
        await using var scope = _provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ILibraryCommands>().RegisterMediaAssetAsync(
            new RegisterMediaAssetRequest(assetId, workId, targetIds, path, 2000, "matroska", [], unitIds));
    }

    private async Task LinkAsync(Guid assetId, IReadOnlyList<Guid> unitIds)
    {
        await using var scope = _provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ILibraryCommands>().LinkAssetUnitsAsync(assetId, unitIds);
    }

    private async Task DeliverMediaAvailableAsync(MediaAvailable domainEvent)
    {
        await using var scope = _provider.CreateAsyncScope();
        foreach (var handler in scope.ServiceProvider.GetServices<IEventHandler<MediaAvailable>>())
        {
            await handler.HandleAsync(domainEvent);
        }
    }

    private async Task<T> QueryAsync<T>(Func<ILibraryQuery, Task<T>> query)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<ILibraryQuery>());
    }

    private async Task DrainAsync()
    {
        while (true)
        {
            var commands = await DrainCommandsAsync();
            var events = await DrainOutboxAsync();
            if (commands == 0 && events == 0)
            {
                break;
            }
        }
    }

    private async Task<int> DrainCommandsAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
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
        await using var scope = _provider.CreateAsyncScope();
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
