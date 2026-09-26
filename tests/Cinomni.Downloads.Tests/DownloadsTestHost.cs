using Cinomni.Acquisition;
using Cinomni.Acquisition.Persistence;
using Cinomni.Downloads;
using Cinomni.Downloads.Application;
using Cinomni.Downloads.Engine;
using Cinomni.Downloads.Persistence;
using Cinomni.Operations;
using Cinomni.Operations.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Downloads.Tests;

/// <summary>
/// Builds a service provider wired with the platform kernel plus the Acquisition and Downloads
/// modules against a real PostgreSQL database (docker-compose.dev.yml). Acquisition is present so
/// the full spine — DownloadQueued → download → feedback → intent — can be driven end to end. Tests
/// supply the <see cref="FakeTorrentEngine"/> in place of the real sidecar; the hosted stream pump
/// does not start under a bare provider, so tests drive transitions deterministically.
/// </summary>
internal static class DownloadsTestHost
{
    public static string ConnectionStringFor(string database) =>
        $"Host=localhost;Port=5442;Database={database};Username=cinomni;Password=cinomni_dev";

    /// <param name="configureTunnel">
    /// The opt-in egress guard. Left alone — as every pre-existing test does — no tunnel is configured,
    /// the watch job is never scheduled and the module behaves exactly as it did before the guard
    /// existed. A test that is about the guard opts in here, the same way a deployment does.
    /// </param>
    public static async Task<ServiceProvider> CreateAsync(
        string database,
        FakeTorrentEngine engine,
        Action<IServiceCollection>? configure = null,
        Action<TunnelOptions>? configureTunnel = null,
        Action<TransferOptions>? configureTransfers = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOperations(ConnectionStringFor(database));
        services.AddAcquisitionModule();
        services.AddDownloadsModule(configureTunnel: configureTunnel, configureTransfers: configureTransfers);

        // Substitute the sidecar with the fake, and register the options the service needs.
        services.AddSingleton(new SidecarOptions { StagingPath = "/data/test-downloads" });
        services.AddSingleton<ITorrentEngine>(engine);
        configure?.Invoke(services);

        var provider = services.BuildServiceProvider();

        await using var scope = provider.CreateAsyncScope();
        var operationsDb = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
        await operationsDb.Database.EnsureDeletedAsync();
        await operationsDb.Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<AcquisitionDbContext>().Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<DownloadsDbContext>().Database.MigrateAsync();

        return provider;
    }
}
