using Cinomni.Acquisition;
using Cinomni.Acquisition.Persistence;
using Cinomni.Catalog.Contracts;
using Cinomni.Import.Application;
using Cinomni.Import.Files;
using Cinomni.Import.Persistence;
using Cinomni.Import.Probe;
using Cinomni.Library;
using Cinomni.Library.Persistence;
using Cinomni.Operations;
using Cinomni.Operations.Persistence;
using Cinomni.ReleaseParsing.Contracts;
using Cinomni.ReleaseParsing.Parsing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Import.Tests;

/// <summary>
/// Builds a service provider wired with the platform kernel plus the Acquisition, Import and Library
/// modules against a real PostgreSQL database (docker-compose.dev.yml). Acquisition is present so the tail of
/// the spine — DownloadCompleted → import → ImportCompleted → intent Available — can be driven end to
/// end. Tests supply the in-memory <see cref="FakeImportFileSystem"/> and <see cref="FakeMediaProbe"/>
/// in place of real disk and ffprobe, plus a <see cref="FakeCatalogSeriesQuery"/> in place of the
/// Catalog schema; the numbering parser is the real one. The command queue and outbox are drained by
/// hand for determinism.
/// </summary>
internal static class ImportTestHost
{
    public static string ConnectionStringFor(string database) =>
        $"Host=localhost;Port=5442;Database={database};Username=cinomni;Password=cinomni_dev";

    public static async Task<ServiceProvider> CreateAsync(
        string database,
        FakeImportFileSystem fileSystem,
        FakeMediaProbe mediaProbe,
        Action<IServiceCollection>? configure = null,
        FakeCatalogSeriesQuery? catalog = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOperations(ConnectionStringFor(database));
        services.AddAcquisitionModule();
        services.AddImportModule();
        // The real Library: Import asks it who holds a path before landing there, and an upgrade's
        // registration is where a same-path replacement used to break.
        services.AddLibraryModule();

        // A low minimum size lets small test files through the specs; a Windows-agnostic root.
        services.AddSingleton(new ImportOptions
        {
            LibraryRoot = "/data/test-library",
            MinVideoBytes = 100,
            MinEpisodeBytes = 100,
        });
        services.AddSingleton<IImportFileSystem>(fileSystem);
        services.AddSingleton<IMediaProbe>(mediaProbe);

        var catalogQuery = catalog ?? new FakeCatalogSeriesQuery();
        services.AddSingleton(catalogQuery);
        services.AddSingleton<ICatalogQuery>(catalogQuery);
        services.AddSingleton<ICatalogSeriesQuery>(catalogQuery);
        services.AddSingleton<IEpisodeNumberParser, EpisodeNumberParser>();
        // The real parser, not a fake: reading the quality off a release name is the behaviour under
        // test in these flows, and a stub would assert nothing about it.
        services.AddSingleton<IReleaseParser, ReleaseParser>();
        configure?.Invoke(services);

        var provider = services.BuildServiceProvider();

        await using var scope = provider.CreateAsyncScope();
        var operationsDb = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
        await operationsDb.Database.EnsureDeletedAsync();
        await operationsDb.Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<AcquisitionDbContext>().Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<ImportDbContext>().Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<LibraryDbContext>().Database.MigrateAsync();

        return provider;
    }
}
