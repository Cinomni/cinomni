using Cinomni.Catalog;
using Cinomni.Catalog.Persistence;
using Cinomni.Discovery.Application;
using Cinomni.Discovery.Persistence;
using Cinomni.Monitoring;
using Cinomni.Monitoring.Persistence;
using Cinomni.Operations;
using Cinomni.Operations.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Discovery.Tests;

/// <summary>
/// Builds a service provider wired with the platform kernel and the Catalog, Monitoring and
/// Discovery modules against a real PostgreSQL database (docker-compose.dev.yml). Tests supply
/// their own <c>IIndexerClient</c> through <paramref name="configure"/> in place of the real
/// Torznab transport.
/// </summary>
internal static class DiscoveryTestHost
{
    public static string ConnectionStringFor(string database) =>
        $"Host=localhost;Port=5442;Database={database};Username=cinomni;Password=cinomni_dev";

    /// <param name="database">A database name unique to the calling test class.</param>
    /// <param name="configure">Extra registrations, typically the fake indexer client.</param>
    /// <param name="configureRetention">Overrides the search-history retention window.</param>
    /// <param name="discoveryMigrationTarget">
    /// When set, the discovery schema is migrated only up to this migration instead of to head. The
    /// expand/contract replay test uses it to land on the pre-partition shape, seed rows, and then
    /// migrate the rest of the way.
    /// </param>
    public static async Task<ServiceProvider> CreateAsync(
        string database,
        Action<IServiceCollection>? configure = null,
        Action<SearchRetentionOptions>? configureRetention = null,
        string? discoveryMigrationTarget = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOperations(ConnectionStringFor(database));
        services.AddCatalogModule();
        services.AddMonitoringModule();
        services.AddDiscoveryModule(configureRetention);
        configure?.Invoke(services);

        var provider = services.BuildServiceProvider();

        await using var scope = provider.CreateAsyncScope();
        var operationsDb = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();

        await operationsDb.Database.EnsureDeletedAsync();
        await operationsDb.Database.MigrateAsync();
        // The module's own entry point: migrates the schema and seeds the default collection, because
        // a work has to sit in one.
        await provider.MigrateCatalogAsync();
        await scope.ServiceProvider.GetRequiredService<MonitoringDbContext>().Database.MigrateAsync();

        var discoveryDb = scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>();
        if (discoveryMigrationTarget is null)
        {
            await provider.MigrateDiscoveryAsync();
        }
        else
        {
            await discoveryDb.GetService<IMigrator>().MigrateAsync(discoveryMigrationTarget);
        }

        return provider;
    }
}
