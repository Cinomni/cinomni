using Cinomni.Catalog.Contracts;
using Cinomni.Kernel.Identifiers;
using Cinomni.Library.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Library.Tests;

/// <summary>
/// The enumeration a path-repair pass reads, executed against real PostgreSQL.
/// <para>
/// It is here rather than beside the pass because the pass's own suite answers this read with a fake,
/// and a projection only fails when EF Core has to translate it into SQL. A query that no test ever
/// sends to a database is a query nobody has run: this one enumerates the library even when the
/// library is empty, so translation is asserted independently of any row.
/// </para>
/// </summary>
public sealed class LibraryVersionPathTests : IAsyncLifetime
{
    private const string Database = "cinomni_test_library_paths";

    private ServiceProvider _provider = null!;

    public async Task InitializeAsync() => _provider = await LibraryTestHost.CreateAsync(Database);

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task An_empty_library_answers_with_no_paths()
    {
        // No rows are needed to break a projection the provider cannot translate, so this is the case
        // that fails first and the one an operator hits on a fresh installation.
        Assert.Empty(await ListPathsAsync());
    }

    [Fact]
    public async Task Every_active_version_is_listed_with_its_asset_and_version()
    {
        var workId = await AddMovieAsync();
        var first = await RegisterAsync(workId, unitId: Uuid7.New(), "/data/library/Zulu/Zulu.mkv");
        var second = await RegisterAsync(workId, unitId: Uuid7.New(), "/data/library/Alpha/Alpha.mkv");

        var paths = await ListPathsAsync();

        Assert.Equal(2, paths.Count);
        var alpha = Assert.Single(paths, p => p.AssetId == second);
        Assert.Equal("/data/library/Alpha/Alpha.mkv", alpha.FullPath);
        Assert.Equal(await PrimaryVersionOfAsync(second), alpha.VersionId);
        var zulu = Assert.Single(paths, p => p.AssetId == first);
        Assert.Equal(await PrimaryVersionOfAsync(first), zulu.VersionId);
    }

    [Fact]
    public async Task The_paths_come_back_ordered_by_path()
    {
        var workId = await AddMovieAsync();
        await RegisterAsync(workId, unitId: Uuid7.New(), "/data/library/Mike/Mike.mkv");
        await RegisterAsync(workId, unitId: Uuid7.New(), "/data/library/Alpha/Alpha.mkv");
        await RegisterAsync(workId, unitId: Uuid7.New(), "/data/library/Zulu/Zulu.mkv");

        var paths = await ListPathsAsync();

        // The order is the contract, not an accident of insertion: a repair pass interrupted half-way
        // resumes over the same sequence, and the ids are minted in insertion order, so a query that
        // fell back to them would still look sorted while being sorted by the wrong thing.
        Assert.Equal(
            ["/data/library/Alpha/Alpha.mkv", "/data/library/Mike/Mike.mkv", "/data/library/Zulu/Zulu.mkv"],
            paths.Select(p => p.FullPath));
    }

    [Fact]
    public async Task An_upgraded_asset_no_longer_names_a_path()
    {
        var workId = await AddMovieAsync();
        var unitId = Uuid7.New();
        var superseded = await RegisterAsync(workId, unitId, "/data/library/Alpha/Alpha.720p.mkv");
        var replacement = await RegisterAsync(workId, unitId, "/data/library/Alpha/Alpha.1080p.mkv");

        // Registering a second asset for the same unit retires the first one in the same transaction.
        var states = await QueryAsync(q => q.GetByWorkAsync(workId));
        Assert.Equal(MediaAssetState.Upgraded, states.Single(a => a.Id.Value == superseded).State);

        var path = Assert.Single(await ListPathsAsync());
        Assert.Equal(replacement, path.AssetId);
        Assert.Equal("/data/library/Alpha/Alpha.1080p.mkv", path.FullPath);
    }

    // -- helpers ---------------------------------------------------------------------------------

    private Task<IReadOnlyList<MediaVersionPath>> ListPathsAsync() =>
        QueryAsync(q => q.ListActiveVersionPathsAsync());

    private async Task<Guid> PrimaryVersionOfAsync(Guid assetId)
    {
        var detail = await QueryAsync(q => q.GetAsync(new MediaAssetId(assetId)));
        return detail!.Asset.PrimaryVersionId!.Value.Value;
    }

    private async Task<Guid> AddMovieAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var commands = scope.ServiceProvider.GetRequiredService<ICatalogCommands>();
        var result = await commands.AddMovieAsync("Path Repair Movie", 2024, []);
        return result.Value.Value;
    }

    private async Task<Guid> RegisterAsync(Guid workId, Guid unitId, string fullPath)
    {
        var assetId = Uuid7.New();
        await using var scope = _provider.CreateAsyncScope();
        var commands = scope.ServiceProvider.GetRequiredService<ILibraryCommands>();
        await commands.RegisterMediaAssetAsync(new RegisterMediaAssetRequest(
            assetId,
            workId,
            TargetIds: [],
            fullPath,
            Size: 2_000_000,
            Container: "matroska,webm",
            Streams:
            [
                new MediaStreamInput(
                    0, MediaStreamType.Video, "h264", null, null, 1920, 1080, null, null,
                    IsDefault: true, IsForced: false),
            ],
            UnitIds: [unitId]));
        return assetId;
    }

    private async Task<T> QueryAsync<T>(Func<ILibraryQuery, Task<T>> query)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<ILibraryQuery>());
    }
}
