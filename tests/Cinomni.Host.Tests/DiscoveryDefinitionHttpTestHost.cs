using Cinomni.Discovery;
using Cinomni.Discovery.Api;
using Cinomni.Identity;
using Cinomni.Identity.Persistence;
using Cinomni.Operations;
using Cinomni.Operations.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text.Json.Serialization;

namespace Cinomni.Host.Tests;

/// <summary>
/// Boots the real Discovery HTTP surface — real routing, real authentication and authorization, the
/// real Discovery module over a real PostgreSQL database — so the definition dry-run route is driven
/// exactly as an operator drives it, and its JSON body is the body an operator reads.
/// <para>
/// No indexer transport is registered at all. That is deliberate: the dry run must never issue a
/// request to a site, so a composition where issuing one is impossible is the strongest available
/// statement of that rule, and the route still has to answer.
/// </para>
/// </summary>
internal static class DiscoveryDefinitionHttpTestHost
{
    public static string ConnectionStringFor(string database) =>
        $"Host=localhost;Port=5442;Database={database};Username=cinomni;Password=cinomni_dev";

    /// <param name="database">A database name unique to the calling test class.</param>
    /// <param name="resetDatabase">
    /// False starts a second host over a database an earlier host already populated, which is how a
    /// restart — the moment a rotated or absent master key first becomes visible — is driven here.
    /// </param>
    /// <param name="configure">Extra registrations after the module's own, e.g. a fake catalog fetcher.</param>
    public static async Task<WebApplication> StartAsync(
        string database, bool resetDatabase = true, Action<IServiceCollection>? configure = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        builder.Services.AddOperations(ConnectionStringFor(database));
        builder.Services.AddIdentityModule();
        builder.Services.AddIdentityAuthentication();
        builder.Services.AddDiscoveryModule();
        configure?.Invoke(builder.Services);
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

        var app = builder.Build();

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var operations = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
            if (resetDatabase)
            {
                await operations.Database.EnsureDeletedAsync();
            }

            await operations.Database.MigrateAsync();
            await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().Database.MigrateAsync();
        }

        await app.Services.MigrateDiscoveryAsync();

        app.UseAuthentication();
        app.UseAuthorization();
        app.MapDiscoveryEndpoints();

        await app.StartAsync();
        return app;
    }
}
