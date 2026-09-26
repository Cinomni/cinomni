using Cinomni.Acquisition;
using Cinomni.Acquisition.Persistence;
using Cinomni.Catalog;
using Cinomni.Catalog.Persistence;
using Cinomni.Decision;
using Cinomni.Discovery;
using Cinomni.Discovery.Indexers;
using Cinomni.Discovery.Persistence;
using Cinomni.Downloads;
using Cinomni.Downloads.Engine;
using Cinomni.Downloads.Persistence;
using Cinomni.Import;
using Cinomni.Import.Application;
using Cinomni.Import.Files;
using Cinomni.Import.Persistence;
using Cinomni.Import.Probe;
using Cinomni.Library;
using Cinomni.Library.Persistence;
using Cinomni.Metadata;
using Cinomni.Metadata.Application;
using Cinomni.Metadata.Persistence;
using Cinomni.Metadata.Providers;
using Cinomni.Monitoring;
using Cinomni.Monitoring.Persistence;
using Cinomni.Operations;
using Cinomni.Operations.Persistence;
using Cinomni.Operations.Settings;
using Cinomni.ReleaseParsing;
using Cinomni.ReleaseParsing.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.SeriesSlice.Tests;

/// <summary>
/// The whole acquisition spine in one service provider: Catalog, Metadata, Monitoring, Discovery,
/// Release Parsing, Decision, Acquisition, Downloads, Import and Library, registered in the same order
/// the Host composes them and migrated against a real PostgreSQL database. Every wave of this slice was
/// built in isolation against contracts; this host is the first place the modules meet.
/// <para>
/// Only the four out-of-process adapters are faked — the indexer transport, the torrent engine, the
/// filesystem and ffprobe — plus the metadata provider (indexer and provider
/// API keys are out of scope, so no real endpoint is reachable). Everything between them is the production
/// code path.
/// </para>
/// <para>
/// The database name is unique to this suite: every <c>TestHost</c> in the repository opens with
/// <c>EnsureDeletedAsync</c> on a hard-coded name and <c>dotnet test</c> parallelises across projects,
/// so reusing another suite's name would drop its database mid-run.
/// </para>
/// </summary>
internal static class SeriesTestHost
{
    /// <summary>Where the fake torrent engine says it staged the download.</summary>
    public const string StagingPath = "/data/test-staging";

    /// <summary>The library root the organiser confines every landed path to.</summary>
    public const string LibraryRoot = "/data/test-library";

    public static string ConnectionStringFor(string database) =>
        $"Host=localhost;Port=5442;Database={database};Username=cinomni;Password=cinomni_dev";

    public static async Task<ServiceProvider> CreateAsync(
        string database,
        FakeSeriesMetadataSource metadata,
        FakeIndexerCatalog indexers,
        FakeTorrentEngine engine,
        FakeSliceFileSystem fileSystem,
        FakeSliceMediaProbe mediaProbe,
        Action<IServiceCollection>? configure = null)
    {
        // Content only ever appears where the engine was told to write it.
        fileSystem.SaveFolders = () => engine.SavePaths;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOperations(ConnectionStringFor(database));

        // Program.cs order, minus Identity (authentication is not part of the spine) and the terminal
        // consumers (Subtitles, Playback, Notifications), which are covered in their own suites.
        services.AddCatalogModule();
        services.AddMetadataModule();
        services.AddMonitoringModule();
        services.AddDiscoveryModule();
        services.AddReleaseParsingModule();
        services.AddDecisionModule();
        services.AddAcquisitionModule();
        services.AddDownloadsModule();
        services.AddImportModule();
        services.AddLibraryModule();

        AddMetadataFakes(services, metadata);
        AddDiscoveryFakes(services, indexers);
        AddDownloadFakes(services, engine);
        AddImportFakes(services, fileSystem, mediaProbe);
        configure?.Invoke(services);

        var provider = services.BuildServiceProvider();
        await MigrateAsync(provider);
        return provider;
    }

    /// <summary>
    /// The refresh profile a test needs: no TTL at all, so a second refresh genuinely re-fetches. The
    /// production 12-hour series TTL would make "a new season arrives in a later snapshot" untestable —
    /// the service would return before ever consulting the provider.
    /// </summary>
    private static void AddMetadataFakes(IServiceCollection services, FakeSeriesMetadataSource metadata)
    {
        var options = new MetadataOptions
        {
            Providers = [FakeSeriesMetadataSource.ProviderName],
            RefreshTtl = TimeSpan.Zero,
            SeriesRefreshTtl = TimeSpan.Zero,
        };
        services.AddSingleton(options);

        // This host bypasses AddMetadataAdapters (it wires a fake provider, not the production ones),
        // which is the only place that registers ILiveOptions<MetadataOptions>. PurgeSnapshotsCommandHandler
        // now depends on that, so this host supplies its own trivial binder: always the same
        // already-configured instance above.
        services.AddLiveOptions<MetadataOptions>(_ => options);

        services.AddSingleton(metadata);
        services.AddSingleton<IMetadataSource>(metadata);
    }

    private static void AddDiscoveryFakes(IServiceCollection services, FakeIndexerCatalog indexers)
    {
        services.AddSingleton(indexers);
        services.AddSingleton<IIndexerClient, FakeIndexerClient>();
    }

    private static void AddDownloadFakes(IServiceCollection services, FakeTorrentEngine engine)
    {
        services.AddSingleton(new SidecarOptions { StagingPath = StagingPath });
        services.AddSingleton<ITorrentEngine>(engine);
    }

    private static void AddImportFakes(
        IServiceCollection services,
        FakeSliceFileSystem fileSystem,
        FakeSliceMediaProbe mediaProbe)
    {
        // Small floors so a synthetic episode file is not mistaken for a sample.
        services.AddSingleton(new ImportOptions
        {
            LibraryRoot = LibraryRoot,
            MinVideoBytes = 100,
            MinEpisodeBytes = 100,
        });
        services.AddSingleton<IImportFileSystem>(fileSystem);
        services.AddSingleton<IMediaProbe>(mediaProbe);
    }

    /// <summary>
    /// Migrates every schema, <c>OperationsDbContext</c> first. The platform tables are what every
    /// module's unit of work writes into, so migrating a module before them fails with a bare
    /// "relation does not exist" that says nothing about the real cause.
    /// </summary>
    private static async Task MigrateAsync(ServiceProvider provider)
    {
        await using (var scope = provider.CreateAsyncScope())
        {
            var services = scope.ServiceProvider;
            var operationsDb = services.GetRequiredService<OperationsDbContext>();
            await operationsDb.Database.EnsureDeletedAsync();
            await operationsDb.Database.MigrateAsync();

            await services.GetRequiredService<CatalogDbContext>().Database.MigrateAsync();
            await services.GetRequiredService<MetadataDbContext>().Database.MigrateAsync();
            await services.GetRequiredService<MonitoringDbContext>().Database.MigrateAsync();
            await services.GetRequiredService<DiscoveryDbContext>().Database.MigrateAsync();
            await services.GetRequiredService<ReleaseParsingDbContext>().Database.MigrateAsync();
            await services.GetRequiredService<AcquisitionDbContext>().Database.MigrateAsync();
            await services.GetRequiredService<DownloadsDbContext>().Database.MigrateAsync();
            await services.GetRequiredService<ImportDbContext>().Database.MigrateAsync();
            await services.GetRequiredService<LibraryDbContext>().Database.MigrateAsync();
        }

        // Decision seeds one acquisition profile per content kind, so it goes through its own path.
        await provider.MigrateDecisionAsync();
    }
}
