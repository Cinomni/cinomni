using Cinomni.Catalog;
using Cinomni.Catalog.Persistence;
using Cinomni.Metadata.Contracts;
using Cinomni.Operations;
using Cinomni.Operations.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Catalog.Tests;

/// <summary>
/// Builds a service provider wired with the platform kernel and the Catalog module against a
/// real PostgreSQL database (docker-compose.dev.yml), with both schemas migrated from scratch.
/// <para>
/// A <see cref="FakeMetadataQuery"/> is always registered: the host carries Operations and Catalog only,
/// yet Catalog's ACL handlers depend on Metadata's read model, and the series structure handler is
/// genuinely resolved and executed by the series suites. <paramref name="configure"/> runs last, so a
/// test can still override anything.
/// </para>
/// </summary>
internal static class CatalogTestHost
{
    public static string ConnectionStringFor(string database) =>
        $"Host=localhost;Port=5442;Database={database};Username=cinomni;Password=cinomni_dev";

    public static async Task<ServiceProvider> CreateAsync(
        string database,
        Action<IServiceCollection>? configure = null,
        FakeMetadataQuery? metadata = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOperations(ConnectionStringFor(database));
        services.AddCatalogModule();
        services.AddSingleton(metadata ?? new FakeMetadataQuery());
        services.AddScoped<IMetadataQuery>(sp => sp.GetRequiredService<FakeMetadataQuery>());
        configure?.Invoke(services);

        var provider = services.BuildServiceProvider();

        await using var scope = provider.CreateAsyncScope();
        var operationsDb = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
        var catalogDb = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();

        await operationsDb.Database.EnsureDeletedAsync();
        await operationsDb.Database.MigrateAsync();
        await catalogDb.Database.MigrateAsync();
        // Seeds the default collection, as the Host's own MigrateCatalogAsync does — a work has to sit
        // somewhere, so every test starts from the shelf an installation starts with.
        await provider.MigrateCatalogAsync();

        return provider;
    }
}
