using Cinomni.Catalog;
using Cinomni.Kernel.Identifiers;
using Cinomni.Library.Contracts;
using Cinomni.Library.Persistence;
using Cinomni.Operations;
using Cinomni.Operations.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Library.Tests;

/// <summary>
/// An upgrade usually lands on the very path the version it replaces names. Only live versions have
/// to name a path no other live version does: counting the retired one too made every such upgrade
/// fail to register, on every retry, and the library kept offering a file that was in the bin.
/// </summary>
public sealed class ReplacedVersionTests : IAsyncLifetime
{
    private const string MoviePath = "/data/library/Film (2024)/Film (2024).mkv";

    private ServiceProvider _provider = null!;

    public async Task InitializeAsync() =>
        _provider = await LibraryTestHost.CreateAsync("cinomni_test_library_replaced_versions");

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task An_upgrade_on_the_same_path_registers_and_retires_what_it_replaced()
    {
        var workId = Uuid7.New();
        var first = await RegisterAsync(workId, MoviePath, size: 1_000);

        var better = await RegisterAsync(workId, MoviePath, size: 4_000);

        await using var scope = _provider.CreateAsyncScope();
        var query = scope.ServiceProvider.GetRequiredService<ILibraryQuery>();
        Assert.Equal(MediaAssetState.Upgraded, (await query.GetAsync(new MediaAssetId(first)))!.Asset.State);
        Assert.Equal(MediaAssetState.Active, (await query.GetAsync(new MediaAssetId(better)))!.Asset.State);
        Assert.Equal(better, (await query.FindActiveByPathAsync(MoviePath))!.Id.Value);

        var retired = await scope.ServiceProvider.GetRequiredService<LibraryDbContext>().Assets
            .AsNoTracking().Where(a => a.Id == first).SelectMany(a => a.Versions).SingleAsync();
        Assert.NotNull(retired.RetiredAt);
        // It still says where the old copy was: the record the household needs to get it back.
        Assert.Equal(MoviePath, retired.FullPath);
    }

    /// <summary>
    /// Assets registered before unit links existed serve no unit on record, so matching by unit alone
    /// never found them: the upgrade that landed on their path failed to register on every retry.
    /// </summary>
    [Fact]
    public async Task An_asset_from_before_unit_links_is_replaced_by_the_upgrade_that_lands_on_its_path()
    {
        var workId = Uuid7.New();
        var legacy = await RegisterAsync(workId, MoviePath, size: 1_000, unitIds: []);

        var better = await RegisterAsync(workId, MoviePath, size: 4_000);

        await using var scope = _provider.CreateAsyncScope();
        var query = scope.ServiceProvider.GetRequiredService<ILibraryQuery>();
        Assert.Equal(MediaAssetState.Upgraded, (await query.GetAsync(new MediaAssetId(legacy)))!.Asset.State);
        Assert.Equal(better, (await query.FindActiveByPathAsync(MoviePath))!.Id.Value);
    }

    [Fact]
    public async Task The_works_with_files_under_a_folder_are_the_ones_with_live_files_there()
    {
        var first = Uuid7.New();
        var second = Uuid7.New();
        var shared = Path.GetFullPath("/data/library/Shared");
        await RegisterAsync(first, Path.Combine(shared, "Shared.mkv"), size: 1);
        await RegisterAsync(second, Path.Combine(shared + " [x]", "Shared [x].mkv"), size: 1);

        await using var scope = _provider.CreateAsyncScope();
        var works = await scope.ServiceProvider.GetRequiredService<ILibraryQuery>().FindActiveWorksUnderAsync(shared);

        // "Shared [x]" merely starts with the same letters: it is a folder of its own.
        Assert.Equal([first], works);
    }

    [Fact]
    public async Task Two_live_versions_still_cannot_name_one_file()
    {
        await RegisterAsync(Uuid7.New(), MoviePath, size: 1_000);

        // Another title, the same path: not an upgrade, and not something the library can hold twice.
        await Assert.ThrowsAsync<DbUpdateException>(() => RegisterAsync(Uuid7.New(), MoviePath, size: 2_000));
    }

    /// <summary>
    /// Installations upgraded before the column existed have replaced versions that were never marked:
    /// the migration retires them, or they would keep blocking the replacement that lands on their path.
    /// </summary>
    [Fact]
    public async Task The_migration_retires_the_versions_of_assets_already_upgraded_away()
    {
        const string database = "cinomni_test_library_retire_migration";
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOperations(LibraryTestHost.ConnectionStringFor(database));
        services.AddCatalogModule();
        services.AddLibraryModule();
        await using var provider = services.BuildServiceProvider();

        await using (var scope = provider.CreateAsyncScope())
        {
            var operations = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
            await operations.Database.EnsureDeletedAsync();
            await operations.Database.MigrateAsync();

            var library = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
            var migrations = library.Database.GetMigrations().ToList();
            var index = migrations.FindIndex(m => m.EndsWith("_RetireReplacedVersions", StringComparison.Ordinal));
            Assert.True(index > 0, "the migration under test was not found");
            await library.GetService<IMigrator>().MigrateAsync(migrations[index - 1]);

            await library.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO library.media_assets (id, work_id, state, created_at, version)
                VALUES ('00000000-0000-7000-8000-0000000000a1', '00000000-0000-7000-8000-000000000001', 'Upgraded', now(), 1),
                       ('00000000-0000-7000-8000-0000000000a2', '00000000-0000-7000-8000-000000000001', 'Active', now(), 0);
                INSERT INTO library.media_versions (id, asset_id, relative_path, full_path, size, quality_json)
                VALUES ('00000000-0000-7000-8000-0000000000b1', '00000000-0000-7000-8000-0000000000a1', 'old.mkv', '/data/library/old.mkv', 1, '{{}}'),
                       ('00000000-0000-7000-8000-0000000000b2', '00000000-0000-7000-8000-0000000000a2', 'new.mkv', '/data/library/new.mkv', 1, '{{}}');
                """);

            await library.Database.MigrateAsync();
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            var versions = await scope.ServiceProvider.GetRequiredService<LibraryDbContext>().Assets
                .AsNoTracking().SelectMany(a => a.Versions).ToDictionaryAsync(v => v.RelativePath, v => v.RetiredAt);
            Assert.NotNull(versions["old.mkv"]);
            Assert.Null(versions["new.mkv"]);
        }
    }

    private async Task<Guid> RegisterAsync(Guid workId, string path, long size, Guid[]? unitIds = null)
    {
        var assetId = Uuid7.New();
        await using var scope = _provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ILibraryCommands>().RegisterMediaAssetAsync(
            new RegisterMediaAssetRequest(
                assetId,
                WorkId: workId,
                TargetIds: [Uuid7.New()],
                FullPath: path,
                Size: size,
                Container: "matroska",
                Streams:
                [
                    new MediaStreamInput(0, MediaStreamType.Video, "h264", null, null, 1920, 1080, null, null, true, false),
                ],
                UnitIds: unitIds ?? [workId]));
        return assetId;
    }
}
