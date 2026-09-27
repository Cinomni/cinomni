using Cinomni.Identity;
using Cinomni.Identity.Persistence;
using Cinomni.Import;
using Cinomni.Import.Api;
using Cinomni.Import.Application;
using Cinomni.Import.Files;
using Cinomni.Import.Persistence;
using Cinomni.Library;
using Cinomni.Library.Persistence;
using Cinomni.Operations;
using Cinomni.Operations.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cinomni.Host.Tests;

/// <summary>
/// Boots the real Import HTTP surface — real routing, real authentication and authorization, the real
/// Import and Library modules — against a real PostgreSQL database, so the path-repair routes are
/// driven exactly as an operator drives them.
/// <para>
/// Library is present as itself and not as a stand-in, which is the whole point: the repair pass reads
/// the library through <c>ILibraryQuery</c>, and the only suites that exercised that read answered it
/// with a fake. A query is only translated into SQL when a provider is asked to run it, so a preview
/// that never reaches PostgreSQL proves nothing about the preview an installation serves.
/// </para>
/// <para>
/// The filesystem is the one collaborator that is faked. It is outside the process and outside the
/// defect, and a library name that is invalid on Windows cannot be created on the machine that runs
/// these tests — so scripting it in memory is what makes the outcome the same on every platform.
/// </para>
/// </summary>
internal static class ImportPathRepairHttpTestHost
{
    /// <summary>The library root every stored path in these tests is confined to.</summary>
    public static readonly string LibraryRoot = Path.GetFullPath("/data/cinomni-path-repair");

    public static string ConnectionStringFor(string database) =>
        $"Host=localhost;Port=5442;Database={database};Username=cinomni;Password=cinomni_dev";

    public static async Task<WebApplication> StartAsync(string database, PathRepairFileSystem fileSystem)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        builder.Services.AddOperations(ConnectionStringFor(database));
        builder.Services.AddIdentityModule();
        builder.Services.AddIdentityAuthentication();
        builder.Services.AddImportModule();
        builder.Services.AddLibraryModule();

        // The adapters the composition root registers, minus the two that touch the world: the local
        // filesystem is replaced above, and ffprobe is never resolved by a repair pass.
        builder.Services.AddSingleton(new ImportOptions { LibraryRoot = LibraryRoot });
        builder.Services.AddSingleton<IImportFileSystem>(fileSystem);

        // This host is started, so its hosted services run; the tests drain the queue themselves.
        builder.Services.WithoutMessageWorkers();

        var app = builder.Build();

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var operations = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
            await operations.Database.EnsureDeletedAsync();
            await operations.Database.MigrateAsync();
            await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().Database.MigrateAsync();
            await scope.ServiceProvider.GetRequiredService<ImportDbContext>().Database.MigrateAsync();
            await scope.ServiceProvider.GetRequiredService<LibraryDbContext>().Database.MigrateAsync();
        }

        app.UseAuthentication();
        app.UseAuthorization();
        app.MapImportEndpoints();

        await app.StartAsync();
        return app;
    }
}
