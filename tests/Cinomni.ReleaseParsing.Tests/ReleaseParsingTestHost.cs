using Cinomni.ReleaseParsing.Persistence;
using Cinomni.Operations;
using Cinomni.Operations.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.ReleaseParsing.Tests;

/// <summary>
/// Builds a service provider wired with the platform kernel and the Release Parsing module against
/// a real PostgreSQL database (docker-compose.dev.yml), with both schemas migrated from scratch.
/// </summary>
internal static class ReleaseParsingTestHost
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
        services.AddReleaseParsingModule();
        configure?.Invoke(services);

        var provider = services.BuildServiceProvider();

        await using var scope = provider.CreateAsyncScope();
        var operationsDb = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
        var parsingDb = scope.ServiceProvider.GetRequiredService<ReleaseParsingDbContext>();

        await operationsDb.Database.EnsureDeletedAsync();
        await operationsDb.Database.MigrateAsync();
        await parsingDb.Database.MigrateAsync();

        return provider;
    }
}
