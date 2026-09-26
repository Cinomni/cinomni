using Cinomni.Catalog;
using Cinomni.Catalog.Persistence;
using Cinomni.Metadata.Application;
using Cinomni.Metadata.Persistence;
using Cinomni.Metadata.Providers;
using Cinomni.Operations;
using Cinomni.Operations.Persistence;
using Cinomni.Operations.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Metadata.Tests;

/// <summary>
/// Builds a service provider wired with the platform kernel plus the Catalog and Metadata modules
/// against a real PostgreSQL database (docker-compose.dev.yml). Catalog is present so the full ACL spine
/// — RefreshMetadata → MetadataRefreshed → Catalog attaches the snapshot to its work — can be driven end
/// to end. Tests supply one or more fake providers; the command queue and outbox are drained by hand for
/// determinism.
/// </summary>
internal static class MetadataTestHost
{
    public static string ConnectionStringFor(string database) =>
        $"Host=localhost;Port=5442;Database={database};Username=cinomni;Password=cinomni_dev";

    /// <param name="configureServices">
    /// Extra registrations applied last, so a test can add a double or an observer (for example a logger
    /// provider that captures the SQL EF Core executes) without a bespoke host.
    /// </param>
    public static async Task<ServiceProvider> CreateAsync(
        string database,
        IReadOnlyList<IMetadataSource> sources,
        Action<MetadataOptions>? configureOptions = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOperations(ConnectionStringFor(database));
        services.AddCatalogModule();
        services.AddMetadataModule();

        var options = new MetadataOptions();
        configureOptions?.Invoke(options);
        services.AddSingleton(options);

        // This host bypasses AddMetadataAdapters (it wires fakes, not the production providers), which
        // is the only place that registers ILiveOptions<MetadataOptions>. PurgeSnapshotsCommandHandler
        // now depends on that, so this host supplies its own trivial binder: always the same
        // already-configured instance above, since no test here exercises the settings store's reload.
        services.AddLiveOptions<MetadataOptions>(_ => options);

        foreach (var source in sources)
        {
            services.AddSingleton(source);
        }

        configureServices?.Invoke(services);

        var serviceProvider = services.BuildServiceProvider();

        await using var scope = serviceProvider.CreateAsyncScope();
        var operationsDb = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
        await operationsDb.Database.EnsureDeletedAsync();
        await operationsDb.Database.MigrateAsync();
        // The module's own entry point: migrates the schema and seeds the default collection, because
        // a work has to sit in one.
        await serviceProvider.MigrateCatalogAsync();
        await scope.ServiceProvider.GetRequiredService<MetadataDbContext>().Database.MigrateAsync();

        return serviceProvider;
    }
}
