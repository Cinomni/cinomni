using Cinomni.Catalog;
using Cinomni.Catalog.Persistence;
using Cinomni.Library;
using Cinomni.Library.Persistence;
using Cinomni.Operations;
using Cinomni.Operations.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Library.Tests;

/// <summary>
/// Builds a service provider wired with the platform kernel plus the Catalog and Library modules
/// against a real PostgreSQL database (docker-compose.dev.yml). Catalog is present so the fan-out of
/// Import's <c>MediaAvailable</c> — Library registers the asset AND Catalog marks the work available —
/// can be driven end to end. The command queue and outbox are drained by hand for determinism.
/// </summary>
internal static class LibraryTestHost
{
    public static string ConnectionStringFor(string database) =>
        $"Host=localhost;Port=5442;Database={database};Username=cinomni;Password=cinomni_dev";

    public static async Task<ServiceProvider> CreateAsync(string database, Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOperations(ConnectionStringFor(database));
        services.AddCatalogModule();
        services.AddLibraryModule();
        configure?.Invoke(services);

        var provider = services.BuildServiceProvider();

        await using var scope = provider.CreateAsyncScope();
        var operationsDb = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
        await operationsDb.Database.EnsureDeletedAsync();
        await operationsDb.Database.MigrateAsync();
        // The module's own entry point: migrates the schema and seeds the default collection, because
        // a work has to sit in one.
        await provider.MigrateCatalogAsync();
        await scope.ServiceProvider.GetRequiredService<LibraryDbContext>().Database.MigrateAsync();

        return provider;
    }
}
