using Cinomni.Catalog;
using Cinomni.Catalog.Persistence;
using Cinomni.Decision;
using Cinomni.Decision.Persistence;
using Cinomni.Discovery;
using Cinomni.Discovery.Persistence;
using Cinomni.Library.Contracts;
using Cinomni.ReleaseParsing;
using Cinomni.ReleaseParsing.Persistence;
using Cinomni.Operations;
using Cinomni.Operations.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Decision.Tests;

/// <summary>
/// Builds a service provider wired with the platform kernel and the Catalog, Discovery, Release
/// Parsing and Decision modules against a real PostgreSQL database. Discovery's Torznab client is
/// registered so its result read-interface resolves (the search transport itself is never exercised
/// here); Catalog is registered so Decision resolves episode coverage through the real
/// <c>ICatalogSeriesQuery</c> rather than a stand-in that could agree with a wrong expectation.
/// </summary>
internal static class DecisionTestHost
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
        services.AddDiscoveryModule();
        services.AddIndexerClients();
        services.AddReleaseParsingModule();
        services.AddDecisionModule();
        // Deciding now asks the library what is already on disk. Nothing is, unless a test says
        // otherwise by registering its own — which is what an upgrade test does.
        services.AddSingleton<ILibraryQuery>(new FakeLibraryQuality());
        configure?.Invoke(services);

        var provider = services.BuildServiceProvider();

        await using (var scope = provider.CreateAsyncScope())
        {
            var operationsDb = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
            await operationsDb.Database.EnsureDeletedAsync();
            await operationsDb.Database.MigrateAsync();
            await scope.ServiceProvider.GetRequiredService<CatalogDbContext>().Database.MigrateAsync();
            await scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>().Database.MigrateAsync();
            await scope.ServiceProvider.GetRequiredService<ReleaseParsingDbContext>().Database.MigrateAsync();
        }

        // Migrate + seed the default profile via the module's own path.
        await provider.MigrateDecisionAsync();

        return provider;
    }
}
