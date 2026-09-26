using Cinomni.Identity;
using Cinomni.Identity.Persistence;
using Cinomni.Operations;
using Cinomni.Operations.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Identity.Tests;

/// <summary>
/// Builds a service provider wired with the platform kernel and the Identity module against a
/// real PostgreSQL database (docker-compose.dev.yml), with both schemas migrated from scratch.
/// The kernel is required because Identity shares its connection and unit of work.
/// </summary>
internal static class IdentityTestHost
{
    public static string ConnectionStringFor(string database) =>
        $"Host=localhost;Port=5442;Database={database};Username=cinomni;Password=cinomni_dev";

    /// <param name="migrate">
    /// False to compose over a database that is already there, the way a restarted process finds it.
    /// The default deletes and re-migrates, which is right for a class that owns its database and
    /// wrong for a test whose whole subject is what survives.
    /// </param>
    public static async Task<ServiceProvider> CreateAsync(
        string database,
        Action<IServiceCollection>? configure = null,
        bool migrate = true)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOperations(ConnectionStringFor(database));
        services.AddIdentityModule();
        configure?.Invoke(services);

        var provider = services.BuildServiceProvider();

        if (!migrate)
        {
            return provider;
        }

        await using var scope = provider.CreateAsyncScope();
        var operationsDb = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
        var identityDb = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();

        await operationsDb.Database.EnsureDeletedAsync();
        await operationsDb.Database.MigrateAsync();
        await identityDb.Database.MigrateAsync();

        return provider;
    }
}
