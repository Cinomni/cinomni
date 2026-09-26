using Cinomni.Catalog;
using Cinomni.Catalog.Persistence;
using Cinomni.Decision.Contracts;
using Cinomni.Import.Contracts;
using Cinomni.Monitoring.Persistence;
using Cinomni.Operations;
using Cinomni.Operations.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Monitoring.Tests;

/// <summary>
/// Builds a service provider wired with the platform kernel, the Catalog module (so tests can
/// catalogue a work and give a series its structure) and the Monitoring module against a real
/// PostgreSQL database (docker-compose.dev.yml), with every schema migrated from scratch.
/// </summary>
internal static class MonitoringTestHost
{
    public static string ConnectionStringFor(string database) =>
        $"Host=localhost;Port=5442;Database={database};Username=cinomni;Password=cinomni_dev";

    public static async Task<ServiceProvider> CreateAsync(
        string database,
        Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOperations(ConnectionStringFor(database));
        services.AddCatalogModule();
        services.AddMonitoringModule();

        // Register the event this module consumes but does not own, so tests can publish it through the
        // outbox (in the Host, Import registers it). Catalog already registers WorkAdded and
        // SeriesStructureChanged, the other two events Monitoring subscribes to.
        services.AddIntegrationEvent<MediaAvailable>(ImportEventNames.MediaAvailable);
        services.AddIntegrationEvent<UpgradeAssessed>(DecisionEventNames.UpgradeAssessed);

        configure?.Invoke(services);

        var provider = services.BuildServiceProvider();

        await using var scope = provider.CreateAsyncScope();
        var operationsDb = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
        var catalogDb = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var monitoringDb = scope.ServiceProvider.GetRequiredService<MonitoringDbContext>();

        await operationsDb.Database.EnsureDeletedAsync();
        await operationsDb.Database.MigrateAsync();
        await catalogDb.Database.MigrateAsync();
        await monitoringDb.Database.MigrateAsync();

        return provider;
    }
}
