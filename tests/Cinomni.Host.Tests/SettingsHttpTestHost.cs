using Cinomni.Catalog;
using Cinomni.Catalog.Persistence;
using Cinomni.Decision;
using Cinomni.Host.Operations;
using Cinomni.Identity;
using Cinomni.Import;
using Cinomni.Subtitles;
using Cinomni.Subtitles.Persistence;
using Cinomni.Identity.Persistence;
using Cinomni.Metadata;
using Cinomni.Monitoring;
using Cinomni.Monitoring.Persistence;
using Cinomni.Operations;
using Cinomni.Operations.Backup;
using Cinomni.Operations.Persistence;
using Cinomni.Playback;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cinomni.Host.Tests;

/// <summary>
/// Boots the real <c>/api/operations/settings</c> HTTP surface — real routing, real authentication and
/// authorization middleware, the real write path — against a real PostgreSQL database. Deliberately
/// narrower than the full <c>AddCinomniModules</c> composition: only the modules that register a
/// settable key (the platform kernel's own <c>RetentionOptions</c>, Decision, Metadata, Monitoring,
/// Catalog, Import, Subtitles, Playback and Backup) plus Identity for a real bearer session, so the test asserts exactly those keys
/// without every module's own hosted service running against a schema this host never migrates.
/// </summary>
internal static class SettingsHttpTestHost
{
    public static string ConnectionStringFor(string database) =>
        $"Host=localhost;Port=5442;Database={database};Username=cinomni;Password=cinomni_dev";

    public static async Task<WebApplication> StartAsync(string database, IConfiguration? configuration = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        var connectionString = ConnectionStringFor(database);
        var config = configuration ?? new ConfigurationBuilder().Build();

        builder.Services.AddOperations(connectionString, configuration: config);
        builder.Services.AddIdentityModule();
        builder.Services.AddIdentityAuthentication();
        builder.Services.AddCatalogModule();
        builder.Services.AddDecisionModule();
        builder.Services.AddImportModule();
        builder.Services.AddSubtitlesModule();
        builder.Services.AddMonitoringModule();
        // Registers the transcoding keys; nothing here resolves a Playback service or touches its schema.
        builder.Services.AddPlaybackModule();
        builder.Services.AddMetadataAdapters(
            configureTmdb: options => options.ApiKey = "unused-in-tests",
            configureTvdb: options => options.ApiKey = "unused-in-tests");
        builder.Services.AddBackup(connectionString);

        var app = builder.Build();

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var operations = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
            await operations.Database.EnsureDeletedAsync();
            await operations.Database.MigrateAsync();
            await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().Database.MigrateAsync();
            await scope.ServiceProvider.GetRequiredService<MonitoringDbContext>().Database.MigrateAsync();
            await scope.ServiceProvider.GetRequiredService<CatalogDbContext>().Database.MigrateAsync();
            await scope.ServiceProvider.GetRequiredService<SubtitlesDbContext>().Database.MigrateAsync();
        }

        app.UseAuthentication();
        app.UseAuthorization();
        app.MapOperationsEndpoints();

        await app.StartAsync();
        return app;
    }
}
