using Cinomni.Catalog.Contracts;
using Cinomni.Downloads.Contracts;
using Cinomni.Import.Application;
using Cinomni.Import.Contracts;
using Cinomni.Import.EventHandlers;
using Cinomni.Import.Files;
using Cinomni.Import.Messaging;
using Cinomni.Import.Persistence;
using Cinomni.Import.Probe;
using Cinomni.Kernel.Messaging;
using Cinomni.Operations;
using Cinomni.Operations.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cinomni.Import;

/// <summary>
/// Registers the Import module: the recoverable job that lands a completed download into the
/// library — match by content, decide against specs, operate the file (hardlink/verify/rollback),
/// analyse with ffprobe, and register a media asset. Reacts to Downloads' <c>DownloadCompleted</c>
/// and closes the acquisition spine by emitting <c>ImportCompleted</c>/<c>ImportFailed</c>. Requires
/// the platform kernel. The filesystem and ffprobe adapters are registered separately
/// (<see cref="AddImportAdapters"/>) so tests can substitute in-memory fakes.
/// <para>
/// Two collaborators come from other modules and must be present in the container: Catalog's
/// <c>ICatalogQuery</c>/<c>ICatalogSeriesQuery</c> (resolving a landed file to its episode) and
/// ReleaseParsing's <c>IEpisodeNumberParser</c> (the platform's single numbering vocabulary).
/// </para>
/// </summary>
public static class ImportModule
{
    /// <summary>How long startup will spend queueing stranded jobs before serving anyway.</summary>
    private static readonly TimeSpan RecoveryBudget = TimeSpan.FromSeconds(30);

    public static IServiceCollection AddImportModule(this IServiceCollection services)
    {
        services.AddModuleDbContext<ImportDbContext>();

        MovieNamingOptions BindNaming(SettingsView view)
        {
            var raw = view.GetString(MovieNamingSettingDefinitions.Format);
            return new MovieNamingOptions
            {
                Format = Enum.TryParse<MovieNamingFormat>(raw, out var format) && Enum.IsDefined(format)
                    ? format
                    : MovieNamingFormat.ReleaseName,
            };
        }

        services.AddSettingsCheck([MovieNamingSettingDefinitions.Format], BindNaming, MovieNamingOptions.Check);
        services.AddLiveOptions(BindNaming);

        services.AddIntegrationEvent<ImportRequested>(ImportEventNames.ImportRequested);
        services.AddIntegrationEvent<ImportCompleted>(ImportEventNames.ImportCompleted);
        services.AddIntegrationEvent<ImportFailed>(ImportEventNames.ImportFailed);
        services.AddIntegrationEvent<MediaAvailable>(ImportEventNames.MediaAvailable);
        services.AddIntegrationEvent<MediaFileRelocated>(ImportEventNames.MediaFileRelocated);

        services.AddCommand<ProcessCompletedDownloadCommand>(ImportCommandNames.ProcessCompletedDownload);
        services.AddCommand<RetryImportJobCommand>(ImportCommandNames.RetryImportJob);
        services.AddCommand<RepairLibraryPathsCommand>(ImportCommandNames.RepairLibraryPaths);
        services.AddCommand<DeleteWorkFilesCommand>(ImportCommandNames.DeleteWorkFiles);

        services.AddScoped<ImportRecovery>();
        services.AddScoped<ImportPolicy>();
        services.AddScoped<EpisodeFileParser>();
        services.AddScoped<TargetResolution>();
        services.AddScoped<LibraryOrganizer>();
        services.AddScoped<LibraryPathSanitizer>();
        services.AddScoped<LibraryPathRepair>();
        services.AddScoped<WorkFileRemoval>();
        services.AddScoped<ReleaseQualityReader>();
        services.AddScoped<IImportProcessor, ImportService>();
        services.AddScoped<IImportQuery, ImportJobQuery>();

        services.AddScoped<ICommandHandler<ProcessCompletedDownloadCommand>, ProcessCompletedDownloadCommandHandler>();
        services.AddScoped<ICommandHandler<RetryImportJobCommand>, RetryImportJobCommandHandler>();
        services.AddScoped<ICommandHandler<RepairLibraryPathsCommand>, RepairLibraryPathsCommandHandler>();
        services.AddScoped<ICommandHandler<DeleteWorkFilesCommand>, DeleteWorkFilesCommandHandler>();

        // Consumes Downloads' DownloadCompleted → enqueue a ProcessCompletedDownload command.
        services.AddScoped<IEventHandler<DownloadCompleted>, DownloadCompletedHandler>();
        services.AddScoped<IEventHandler<WorkRemoved>, WorkRemovedHandler>();

        return services;
    }

    /// <summary>
    /// Registers the production filesystem and ffprobe adapters. Tests skip this and register their
    /// own <see cref="IImportFileSystem"/>/<see cref="IMediaProbe"/> fakes, plus the options.
    /// </summary>
    public static IServiceCollection AddImportAdapters(
        this IServiceCollection services,
        Action<ImportOptions>? configureImport = null,
        Action<FfprobeOptions>? configureFfprobe = null)
    {
        var importOptions = new ImportOptions();
        configureImport?.Invoke(importOptions);
        services.AddSingleton(importOptions);

        var ffprobeOptions = new FfprobeOptions();
        configureFfprobe?.Invoke(ffprobeOptions);
        services.AddSingleton(ffprobeOptions);

        services.AddSingleton<IImportFileSystem, LocalImportFileSystem>();
        services.AddSingleton<IMediaProbe, FfprobeMediaProbe>();
        return services;
    }

    /// <summary>Applies pending migrations for the import schema (idempotent).</summary>
    public static async Task MigrateImportAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ImportDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);
    }

    /// <summary>
    /// Startup recovery: queues a re-drive for every import job a crash left in <c>Pending</c>
    /// (see <see cref="ImportRecovery"/>). Idempotent, and cheap — it queues commands, it does not
    /// touch the filesystem.
    /// <para>
    /// It cannot stop the installation from starting. A failure here means some downloads stay
    /// un-imported until the next start, which is exactly the state recovery found them in; refusing
    /// to serve the library over it would be a strictly worse outcome. The same reasoning bounds it in
    /// time: whatever is not queued inside <see cref="RecoveryBudget"/> — a database under load, a
    /// lock on <c>import.import_jobs</c> — waits for the next start rather than holding the boot open.
    /// </para>
    /// </summary>
    public static async Task RecoverImportsAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ImportModule));

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(RecoveryBudget);

        try
        {
            await using var scope = services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ImportRecovery>().RecoverAsync(budget.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "Import recovery ran out of its {Budget} budget; the jobs it did not reach are unchanged "
                + "and are queued on the next start.",
                RecoveryBudget);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Import recovery failed; pending jobs were left for the next start.");
        }
    }
}
