using Cinomni.Acquisition;
using Cinomni.Acquisition.Persistence;
using Cinomni.Operations;
using Cinomni.Operations.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Acquisition.Tests;

/// <summary>
/// Builds a service provider wired with the platform kernel and the Acquisition module against a
/// real PostgreSQL database. Acquisition is self-contained on the write side (it only touches its
/// own schema), so no other domain module is registered — the upstream events (MonitoringEnabled,
/// ReleaseSelected) are fed by invoking their handlers directly, as Decision's tests do.
/// </summary>
internal static class AcquisitionTestHost
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
        services.AddAcquisitionModule();
        configure?.Invoke(services);

        var provider = services.BuildServiceProvider();

        await using (var scope = provider.CreateAsyncScope())
        {
            var operationsDb = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
            await operationsDb.Database.EnsureDeletedAsync();
            await operationsDb.Database.MigrateAsync();
            await scope.ServiceProvider.GetRequiredService<AcquisitionDbContext>().Database.MigrateAsync();
        }

        return provider;
    }
}
