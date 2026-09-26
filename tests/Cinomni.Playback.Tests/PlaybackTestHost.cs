using Cinomni.Catalog;
using Cinomni.Catalog.Persistence;
using Cinomni.Library;
using Cinomni.Library.Persistence;
using Cinomni.Operations;
using Cinomni.Operations.Persistence;
using Cinomni.Playback;
using Cinomni.Playback.Application;
using Cinomni.Playback.Encoding;
using Cinomni.Playback.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Playback.Tests;

/// <summary>
/// Builds a service provider wired with the platform kernel plus the Library, Catalog and Playback
/// modules against a real PostgreSQL database (docker-compose.dev.yml). Library is present so Playback
/// can query a real registered asset (by interface) to plan against; Catalog is present because "next
/// up" walks the real season/episode ordering rather than a stand-in that could agree with a wrong
/// expectation. Tests supply the <see cref="FakeMediaEncoder"/> in place of FFmpeg; the outbox is
/// drained by hand for determinism.
/// </summary>
internal static class PlaybackTestHost
{
    public static string ConnectionStringFor(string database) =>
        $"Host=localhost;Port=5442;Database={database};Username=cinomni;Password=cinomni_dev";

    public static async Task<ServiceProvider> CreateAsync(
        string database,
        FakeMediaEncoder encoder,
        Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOperations(ConnectionStringFor(database));
        services.AddLibraryModule();
        services.AddCatalogModule();
        services.AddPlaybackModule();

        services.AddSingleton(new PlaybackOptions { TranscodeRoot = "/data/test-transcodes" });
        services.AddSingleton<IMediaEncoder>(encoder);
        configure?.Invoke(services);

        var provider = services.BuildServiceProvider();

        await using var scope = provider.CreateAsyncScope();
        var operationsDb = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
        await operationsDb.Database.EnsureDeletedAsync();
        await operationsDb.Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<LibraryDbContext>().Database.MigrateAsync();
        // The module's own entry point: migrates the schema and seeds the default collection, because
        // a work has to sit in one.
        await provider.MigrateCatalogAsync();
        await scope.ServiceProvider.GetRequiredService<PlaybackDbContext>().Database.MigrateAsync();

        return provider;
    }
}
