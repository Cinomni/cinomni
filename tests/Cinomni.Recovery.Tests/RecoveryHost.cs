using Cinomni.Acquisition;
using Cinomni.Acquisition.Persistence;
using Cinomni.Catalog.Contracts;
using Cinomni.Downloads;
using Cinomni.Downloads.Contracts;
using Cinomni.Downloads.Engine;
using Cinomni.Downloads.Persistence;
using Cinomni.Import;
using Cinomni.Import.Application;
using Cinomni.Import.Contracts;
using Cinomni.Import.Files;
using Cinomni.Import.Persistence;
using Cinomni.Import.Probe;
using Cinomni.Kernel.Messaging;
using Cinomni.Library;
using Cinomni.Library.Persistence;
using Cinomni.Operations;
using Cinomni.Operations.Persistence;
using Cinomni.Recovery.Tests.Fakes;
using Cinomni.ReleaseParsing.Contracts;
using Cinomni.ReleaseParsing.Parsing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Recovery.Tests;

/// <summary>
/// Everything that survives a restart, and nothing that does not.
/// <para>
/// The fakes stand for the two things outside the backend process that genuinely outlive it: the
/// libtorrent sidecar and the filesystem. They are created once per test class and handed to every
/// service provider that class builds, so a restart destroys the backend and leaves the world it was
/// working on intact — which is the only arrangement under which "did the checkpoint survive" and
/// "did the file survive" are questions worth asking.
/// </para>
/// <para>
/// The event sinks live here too, for the same reason: they record what the whole run announced, and
/// resetting them at a restart would make "announced exactly once, across the restart" unassertable.
/// </para>
/// </summary>
internal sealed class RecoveryFakes
{
    public RecoveryTorrentEngine Engine { get; } = new();

    public RecoveryFileSystem FileSystem { get; } = new();

    public RecoveryFakes() =>
        // Content only ever appears where the engine was told to write it.
        FileSystem.SaveFolders = () => Engine.SavePaths;

    public RecoveryMediaProbe MediaProbe { get; } = new();

    public RecoveryCatalog Catalog { get; } = new();

    public RecoveryEventSink Events { get; } = new();

    /// <summary>
    /// The opt-in egress guard, applied to every provider this class builds. Left null — as every
    /// other class here leaves it — no tunnel is configured, no download is ever held, and the module
    /// behaves exactly as it does without the guard. A class that is about the guard opts in, the same
    /// way a deployment does, and the setting has to survive a restart because a deployment's does.
    /// </summary>
    public Action<TunnelOptions>? ConfigureTunnel { get; set; }

    /// <summary>Applied last when a provider is built, so a test can add its own registrations.</summary>
    public Action<IServiceCollection>? Configure { get; set; }
}

/// <summary>
/// Builds and rebuilds the backend against one PostgreSQL database.
/// <para>
/// The restart primitive is <see cref="RestartAsync"/>: it disposes the service provider and builds a
/// new one <b>without touching the database</b>, then runs the same recovery sequence
/// <c>Program.cs</c> runs. Every <c>TestHost</c> in this repository resets its database at
/// construction, which is right for a suite about behaviour and makes a suite about recovery
/// impossible — so this one takes a flag instead, and every path that does not restart still resets.
/// </para>
/// </summary>
internal static class RecoveryHost
{
    /// <summary>Where the fake engine stages what it downloads.</summary>
    public const string StagingPath = "/data/recovery-staging";

    /// <summary>The library root every landed path is confined to.</summary>
    public const string LibraryRoot = "/data/recovery-library";

    public static string ConnectionStringFor(string database) =>
        $"Host=localhost;Port=5442;Database={database};Username=cinomni;Password=cinomni_dev";

    /// <summary>
    /// Builds the backend over <paramref name="database"/>. With <paramref name="reset"/> the database
    /// is dropped and migrated from nothing, which is what every test class does once at the start;
    /// without it the schemas are migrated in place and whatever is there survives, which is what a
    /// restart is.
    /// </summary>
    public static async Task<ServiceProvider> CreateAsync(
        string database,
        RecoveryFakes fakes,
        bool reset = true)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOperations(ConnectionStringFor(database));

        // Program.cs order for the modules whose work outlives the process. The cataloguing half of
        // the spine (Catalog, Metadata, Monitoring, Discovery, Decision) is deliberately absent: it is
        // proven end to end by Cinomni.SeriesSlice.Tests, it holds nothing across a restart that these
        // modules do not, and standing it up would double the runtime of every restart in this suite.
        services.AddAcquisitionModule();
        services.AddDownloadsModule(configureTunnel: fakes.ConfigureTunnel);
        services.AddImportModule();
        services.AddLibraryModule();

        services.AddSingleton(new SidecarOptions { StagingPath = StagingPath });
        services.AddSingleton<ITorrentEngine>(fakes.Engine);

        // Small floors so a synthetic file is not rejected as a sample. The staging root is the
        // sidecar's own, exactly as the composition root wires it: an import may only read from the
        // directory downloads are staged in, and a suite that left it unset would never exercise that.
        services.AddSingleton(new ImportOptions
        {
            LibraryRoot = LibraryRoot,
            StagingRoot = StagingPath,
            MinVideoBytes = 100,
            MinEpisodeBytes = 100,
        });
        services.AddSingleton<IImportFileSystem>(fakes.FileSystem);
        services.AddSingleton<IMediaProbe>(fakes.MediaProbe);
        services.AddSingleton(fakes.Catalog);
        services.AddSingleton<ICatalogQuery>(fakes.Catalog);
        services.AddSingleton<ICatalogSeriesQuery>(fakes.Catalog);

        // The real numbering and release parsers: resolving a staged file to its episode is part of
        // what a re-drive has to get right a second time, and a stub would assert nothing about it.
        services.AddSingleton<IEpisodeNumberParser, EpisodeNumberParser>();
        services.AddSingleton<IReleaseParser, ReleaseParser>();

        services.AddSingleton(fakes.Events);
        services.AddScoped<IEventHandler<MediaAvailable>, RecoveryEventSink.MediaAvailableRecorder>();
        services.AddScoped<IEventHandler<ImportCompleted>, RecoveryEventSink.ImportCompletedRecorder>();
        services.AddScoped<IEventHandler<ImportFailed>, RecoveryEventSink.ImportFailedRecorder>();
        services.AddScoped<IEventHandler<DownloadCompleted>, RecoveryEventSink.DownloadCompletedRecorder>();
        services.AddScoped<IEventHandler<MetadataReady>, RecoveryEventSink.MetadataReadyRecorder>();

        fakes.Configure?.Invoke(services);

        var provider = services.BuildServiceProvider();
        await MigrateAsync(provider, reset);
        return provider;
    }

    /// <summary>
    /// A process restart. The old provider is disposed first and awaited, so its pooled connection is
    /// released before the new one opens — a leaked scope across this boundary shows up as an
    /// intermittent "connection already in use" rather than as the failure it really is.
    /// <para>
    /// What runs afterwards is <c>Program.cs</c>'s recovery sequence, in its order and through its own
    /// entry points. A test that called the recovery services directly would prove the services work
    /// and say nothing about whether the Host actually runs them.
    /// </para>
    /// </summary>
    public static async Task<ServiceProvider> RestartAsync(
        ServiceProvider previous,
        string database,
        RecoveryFakes fakes)
    {
        await previous.DisposeAsync();

        var next = await CreateAsync(database, fakes, reset: false);
        await next.RecoverOperationsAsync();
        await next.RecoverDownloadsAsync();
        await next.RecoverImportsAsync();
        return next;
    }

    /// <summary>
    /// Migrates every schema, the platform's first: the outbox and command tables are what every
    /// module's unit of work writes into, so migrating a module before them fails with a bare
    /// "relation does not exist" that says nothing about the cause.
    /// </summary>
    private static async Task MigrateAsync(ServiceProvider provider, bool reset)
    {
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;

        var operationsDb = services.GetRequiredService<OperationsDbContext>();
        if (reset)
        {
            await operationsDb.Database.EnsureDeletedAsync();
        }

        await operationsDb.Database.MigrateAsync();
        await services.GetRequiredService<AcquisitionDbContext>().Database.MigrateAsync();
        await services.GetRequiredService<DownloadsDbContext>().Database.MigrateAsync();
        await services.GetRequiredService<ImportDbContext>().Database.MigrateAsync();
        await services.GetRequiredService<LibraryDbContext>().Database.MigrateAsync();
    }
}
