using Cinomni.Catalog;
using Cinomni.Catalog.Persistence;
using Cinomni.Kernel.Messaging;
using Cinomni.Metadata.Contracts;
using Cinomni.Monitoring.Contracts;
using Cinomni.Operations;
using Cinomni.Operations.Persistence;
using Cinomni.Requests.Contracts;
using Cinomni.Requests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Requests.Tests;

/// <summary>
/// Builds a service provider wired with the platform kernel plus the Catalog and Requests modules against a
/// real PostgreSQL database (docker-compose.dev.yml). Catalog is real — an approved request has to end up
/// as an actual work — while the metadata provider is faked, so no test touches the network. A sink
/// subscribes to the module's own events so tests can assert what other modules would receive. The command
/// queue and outbox are drained by hand.
/// </summary>
internal static class RequestsTestHost
{
    public static string ConnectionStringFor(string database) =>
        $"Host=localhost;Port=5442;Database={database};Username=cinomni;Password=cinomni_dev";

    /// <param name="configuration">
    /// Extra configuration for the settings store, so a test can stand at a chosen request quota.
    /// Absent means the shipped defaults, which is what every other test in this project wants.
    /// </param>
    public static async Task<ServiceProvider> CreateAsync(
        string database,
        FakeMetadataRefresh metadata,
        RequestEventSink events,
        IConfiguration? configuration = null,
        FakeMonitoring? monitoringFake = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOperations(
            ConnectionStringFor(database),
            configuration: configuration ?? new ConfigurationBuilder().Build());
        services.AddCatalogModule();
        services.AddRequestsModule();

        services.AddSingleton<IMetadataRefresh>(metadata);

        // Monitoring is faked: what matters here is whether fulfilling a request asks for the title to
        // be watched, not how Monitoring then materialises it.
        var monitoring = monitoringFake ?? new FakeMonitoring();
        services.AddSingleton<IMonitoringQuery>(monitoring);
        services.AddSingleton<IMonitoringCommands>(monitoring);

        services.AddSingleton(events);
        services.AddScoped<IEventHandler<MediaRequested>, RequestedSink>();
        services.AddScoped<IEventHandler<MediaRequestApproved>, ApprovedSink>();
        services.AddScoped<IEventHandler<MediaRequestRejected>, RejectedSink>();

        var serviceProvider = services.BuildServiceProvider();

        await using var scope = serviceProvider.CreateAsyncScope();
        var operationsDb = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
        await operationsDb.Database.EnsureDeletedAsync();
        await operationsDb.Database.MigrateAsync();
        // The module's own entry point: migrates the schema and seeds the default collection, because
        // a work has to sit in one.
        await serviceProvider.MigrateCatalogAsync();
        await scope.ServiceProvider.GetRequiredService<RequestsDbContext>().Database.MigrateAsync();

        return serviceProvider;
    }
}
