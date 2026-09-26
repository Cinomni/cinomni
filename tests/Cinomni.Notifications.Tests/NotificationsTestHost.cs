using Cinomni.Acquisition.Contracts;
using Cinomni.Catalog;
using Cinomni.Catalog.Persistence;
using Cinomni.Downloads.Contracts;
using Cinomni.Import.Contracts;
using Cinomni.Metadata.Contracts;
using Cinomni.Notifications.Delivery;
using Cinomni.Notifications.Persistence;
using Cinomni.Operations;
using Cinomni.Operations.Persistence;
using Cinomni.Requests.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Notifications.Tests;

/// <summary>
/// Builds a service provider wired with the platform kernel plus the Catalog and Notifications modules
/// against a real PostgreSQL database (docker-compose.dev.yml). Catalog is present so a notification can
/// resolve a real work title (→i). The consumed events are registered so tests can publish them through
/// the outbox. Tests supply a fake dispatcher; the command queue and outbox are drained by hand.
/// </summary>
internal static class NotificationsTestHost
{
    public static string ConnectionStringFor(string database) =>
        $"Host=localhost;Port=5442;Database={database};Username=cinomni;Password=cinomni_dev";

    public static async Task<ServiceProvider> CreateAsync(string database, FakeNotificationDispatcher dispatcher)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOperations(ConnectionStringFor(database));
        services.AddCatalogModule();
        services.AddNotificationsModule();

        // Register the events this module consumes so tests can publish them through the outbox
        // (in the Host these are registered by their owning modules).
        services.AddIntegrationEvent<MediaAvailable>(ImportEventNames.MediaAvailable);
        services.AddIntegrationEvent<AcquisitionFailed>(AcquisitionEventNames.AcquisitionFailed);
        services.AddIntegrationEvent<ProviderDegraded>(MetadataEventNames.ProviderDegraded);
        services.AddIntegrationEvent<MediaRequested>(RequestEventNames.MediaRequested);
        services.AddIntegrationEvent<TunnelEgressLost>(DownloadEventNames.TunnelEgressLost);
        services.AddIntegrationEvent<TunnelEgressRestored>(DownloadEventNames.TunnelEgressRestored);

        services.AddSingleton<INotificationDispatcher>(dispatcher);

        var serviceProvider = services.BuildServiceProvider();

        await using var scope = serviceProvider.CreateAsyncScope();
        var operationsDb = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
        await operationsDb.Database.EnsureDeletedAsync();
        await operationsDb.Database.MigrateAsync();
        // The module's own entry point: migrates the schema and seeds the default collection, because
        // a work has to sit in one.
        await serviceProvider.MigrateCatalogAsync();
        await scope.ServiceProvider.GetRequiredService<NotificationsDbContext>().Database.MigrateAsync();

        return serviceProvider;
    }
}
