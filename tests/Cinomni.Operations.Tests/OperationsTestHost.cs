using Cinomni.Operations;
using Cinomni.Operations.Persistence;
using Cinomni.Operations.Retention;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Operations.Tests;

/// <summary>
/// Builds a service provider wired with the Operations kernel against a real PostgreSQL
/// database (docker-compose.dev.yml), migrated from scratch. Each test class uses its own
/// database name so classes can run in parallel without interfering.
/// </summary>
internal static class OperationsTestHost
{
    public static string ConnectionStringFor(string database) =>
        $"Host=localhost;Port=5442;Database={database};Username=cinomni;Password=cinomni_dev";

    public static async Task<ServiceProvider> CreateAsync(
        string database,
        Action<IServiceCollection> configure,
        Action<RetentionOptions>? configureRetention = null,
        IConfiguration? configuration = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOperations(ConnectionStringFor(database), configureRetention, configuration);
        configure(services);

        var provider = services.BuildServiceProvider();

        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
        await dbContext.Database.EnsureDeletedAsync();
        await dbContext.Database.MigrateAsync();

        return provider;
    }
}
