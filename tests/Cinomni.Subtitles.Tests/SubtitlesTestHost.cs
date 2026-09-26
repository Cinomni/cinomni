using Cinomni.Catalog;
using Cinomni.Catalog.Persistence;
using Cinomni.Library;
using Cinomni.Library.Persistence;
using Cinomni.Operations;
using Cinomni.Operations.Persistence;
using Cinomni.Subtitles;
using Cinomni.Subtitles.Application;
using Cinomni.Subtitles.Files;
using Cinomni.Subtitles.Persistence;
using Cinomni.Subtitles.Providers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Subtitles.Tests;

/// <summary>
/// Builds a service provider wired with the platform kernel plus the Library, Catalog and Subtitles
/// modules against a real PostgreSQL database (docker-compose.dev.yml). Library is present so the full
/// spine — MediaAssetRegistered → search → SubtitleAvailable → Library enriches the asset — can be
/// driven end to end; Catalog is present so an episode query resolves its <c>SxxEyy</c> through the
/// real <c>ICatalogSeriesQuery</c> rather than a stand-in that could agree with a wrong expectation.
/// Tests supply the fake provider and file store; the command queue and outbox are drained by hand for
/// determinism.
/// </summary>
internal static class SubtitlesTestHost
{
    public static string ConnectionStringFor(string database) =>
        $"Host=localhost;Port=5442;Database={database};Username=cinomni;Password=cinomni_dev";

    public static async Task<ServiceProvider> CreateAsync(
        string database,
        FakeSubtitleProvider provider,
        FakeSubtitleFileStore fileStore,
        SubtitleOptions? options = null,
        Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOperations(ConnectionStringFor(database));
        services.AddLibraryModule();
        services.AddCatalogModule();
        services.AddSubtitlesModule();

        // The default profile disables the provider stagger: only the test that asserts it wants to pay
        // for it, and everything else would otherwise sit in a real two-second delay per search.
        services.AddSingleton(options ?? new SubtitleOptions
        {
            WantedLanguages = ["en"],
            MinScore = 5,
            ProviderCallInterval = TimeSpan.Zero,
        });
        services.AddSingleton<ISubtitleProvider>(provider);
        services.AddSingleton<ISubtitleFileStore>(fileStore);
        configure?.Invoke(services);

        var serviceProvider = services.BuildServiceProvider();

        await using var scope = serviceProvider.CreateAsyncScope();
        var operationsDb = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
        await operationsDb.Database.EnsureDeletedAsync();
        await operationsDb.Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<LibraryDbContext>().Database.MigrateAsync();
        // The module's own entry point: migrates the schema and seeds the default collection, because
        // a work has to sit in one.
        await serviceProvider.MigrateCatalogAsync();
        await scope.ServiceProvider.GetRequiredService<SubtitlesDbContext>().Database.MigrateAsync();

        return serviceProvider;
    }
}
