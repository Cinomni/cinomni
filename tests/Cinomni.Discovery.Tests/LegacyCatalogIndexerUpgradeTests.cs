using Cinomni.Discovery.Contracts;
using Cinomni.Discovery.Persistence;
using Cinomni.Kernel.Identifiers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Discovery.Tests;

/// <summary>
/// The upgrade an 0.1.0-alpha installation takes. Its indexers installed from the catalog that used to
/// ship built in carry a catalog key, a FlareSolverr setting and a snapshotted definition, and nothing
/// in Cinomni resolves that key any more. They must keep working exactly as they did, and read as
/// plain definition indexers from then on.
/// </summary>
public sealed class LegacyCatalogIndexerUpgradeTests : IAsyncLifetime
{
    /// <summary>The last migration before catalog sources.</summary>
    private const string AlphaSchema = "20260924125435_RecordSearchResultIndexerId";

    private readonly Guid _indexerId = Uuid7.New();
    private readonly Guid _definitionId = Uuid7.New();
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync() =>
        _provider = await DiscoveryTestHost.CreateAsync(
            "cinomni_test_discovery_legacy_catalog",
            services =>
            {
                services.AddSingleton<FakeIndexerCatalog>();
                services.AddSingleton<Indexers.IIndexerClient, FakeIndexerClient>();
                services.AddSingleton<Application.IIndexerCatalogFetcher>(new FakeCatalogFetcher());
            },
            discoveryMigrationTarget: AlphaSchema);

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task An_indexer_installed_from_the_old_built_in_catalog_survives_the_upgrade_unchanged()
    {
        await SeedAlphaIndexerAsync();

        await using (var migrate = _provider.CreateAsyncScope())
        {
            await migrate.ServiceProvider.GetRequiredService<DiscoveryDbContext>().GetService<IMigrator>().MigrateAsync();
        }

        await using var scope = _provider.CreateAsyncScope();
        var admin = scope.ServiceProvider.GetRequiredService<IIndexerAdministration>();
        var db = scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>();

        var summary = Assert.Single(await admin.ListIndexersAsync());
        Assert.Equal("legacy-key", summary.CatalogKey);
        Assert.Equal(1, summary.CatalogVersion);
        Assert.Null(summary.CatalogSourceId);
        Assert.True(summary.Enabled);
        Assert.Equal(new IndexerDefinitionId(_definitionId), summary.DefinitionId);
        Assert.True(summary.Settings!.UseFlareSolverr);

        // Its definition is still there and still runs.
        var tested = await admin.TestIndexerAsync(new IndexerId(_indexerId));
        Assert.True(tested.Value.Succeeded);

        // Nothing is listed: no source is subscribed, and nothing was invented for the old key.
        Assert.Empty(await admin.ListCatalogAsync());
        Assert.Empty(await db.IndexerCatalogSources.ToListAsync());

        // With no snapshot to read the old requirement from, it is a plain definition indexer: the
        // administrator may turn FlareSolverr off, which the removed catalog used to refuse.
        var settings = await admin.SetSettingsAsync(new IndexerId(_indexerId), new IndexerSettings(1, true));
        Assert.True(settings.IsSuccess);
        Assert.False((await db.Indexers.AsNoTracking().SingleAsync()).UseFlareSolverr);
    }

    private async Task SeedAlphaIndexerAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>();
        var now = DateTimeOffset.UtcNow;

        // Raw SQL against the alpha schema: the current model has columns that do not exist yet.
        await db.Database.ExecuteSqlAsync(
            $"""
             INSERT INTO discovery.indexer_definitions (id, name, schema_version, content_hash, raw_content, created_at)
             VALUES ({_definitionId}, {"Legacy catalog v1"}, {1}, {"00"}, {CatalogManifests.Definition}, {now})
             """);
        await db.Database.ExecuteSqlAsync(
            $"""
             INSERT INTO discovery.indexers
                 (id, name, protocol, base_url, priority, enabled, supports_movie_search, supports_tv_search,
                  definition_id, catalog_key, catalog_version, minimum_seeders, prefer_magnet, limits_unit,
                  use_flare_solverr, created_at)
             VALUES ({_indexerId}, {"Legacy"}, {"Definition"}, {"https://indexer.example/"}, {25}, {true}, {true}, {true},
                     {_definitionId}, {"legacy-key"}, {1}, {1}, {true}, {"Day"}, {true}, {now})
             """);
    }
}
