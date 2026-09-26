using Cinomni.Acquisition.Application;
using Cinomni.Acquisition.Contracts;
using Cinomni.Acquisition.EventHandlers;
using Cinomni.Acquisition.Messaging;
using Cinomni.Acquisition.Persistence;
using Cinomni.Decision.Contracts;
using Cinomni.Downloads.Contracts;
using Cinomni.Import.Contracts;
using Cinomni.Kernel.Messaging;
using Cinomni.Monitoring.Contracts;
using Cinomni.Operations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Acquisition;

/// <summary>
/// Registers the Acquisition module: the persistent goal of getting content, separated from
/// the download itself. Reacts to Monitoring's <c>MonitoringEnabled</c> (born the goal)
/// and Decision's <c>ReleaseSelected</c> (feed a candidate), and hands downloads off via
/// <c>DownloadQueued</c>. Requires the platform kernel.
/// </summary>
public static class AcquisitionModule
{
    public static IServiceCollection AddAcquisitionModule(this IServiceCollection services)
    {
        services.AddModuleDbContext<AcquisitionDbContext>();

        services.AddIntegrationEvent<AcquisitionRequested>(AcquisitionEventNames.AcquisitionRequested);
        services.AddIntegrationEvent<CandidateSelected>(AcquisitionEventNames.CandidateSelected);
        services.AddIntegrationEvent<DownloadQueued>(AcquisitionEventNames.DownloadQueued);
        services.AddIntegrationEvent<AcquisitionSucceeded>(AcquisitionEventNames.AcquisitionSucceeded);
        services.AddIntegrationEvent<AcquisitionFailed>(AcquisitionEventNames.AcquisitionFailed);
        services.AddIntegrationEvent<AcquisitionRetrying>(AcquisitionEventNames.AcquisitionRetrying);
        services.AddIntegrationEvent<AcquisitionAttemptFailed>(AcquisitionEventNames.AcquisitionAttemptFailed);
        services.AddIntegrationEvent<AcquisitionCancelled>(AcquisitionEventNames.AcquisitionCancelled);

        services.AddCommand<CreateAcquisitionIntentCommand>(AcquisitionCommandNames.CreateAcquisitionIntent);
        services.AddCommand<SelectCandidateCommand>(AcquisitionCommandNames.SelectCandidate);
        services.AddCommand<MarkDownloadStartedCommand>(AcquisitionCommandNames.MarkDownloadStarted);
        services.AddCommand<MarkDownloadCompletedCommand>(AcquisitionCommandNames.MarkDownloadCompleted);
        services.AddCommand<MarkDownloadFailedCommand>(AcquisitionCommandNames.MarkDownloadFailed);
        services.AddCommand<MarkDownloadNotStartedCommand>(AcquisitionCommandNames.MarkDownloadNotStarted);
        services.AddCommand<MarkImportedCommand>(AcquisitionCommandNames.MarkImported);
        services.AddCommand<MarkImportFailedCommand>(AcquisitionCommandNames.MarkImportFailed);
        services.AddCommand<MarkUnitsSatisfiedCommand>(AcquisitionCommandNames.MarkUnitsSatisfied);
        services.AddCommand<CancelWorkGoalsCommand>(AcquisitionCommandNames.CancelWorkGoals);

        services.AddScoped<IAcquisitionCommands, AcquisitionService>();
        services.AddScoped<IAcquisitionQuery, AcquisitionIntentQuery>();

        services.AddScoped<ICommandHandler<CreateAcquisitionIntentCommand>, CreateAcquisitionIntentCommandHandler>();
        services.AddScoped<ICommandHandler<SelectCandidateCommand>, SelectCandidateCommandHandler>();
        services.AddScoped<ICommandHandler<MarkDownloadStartedCommand>, MarkDownloadStartedCommandHandler>();
        services.AddScoped<ICommandHandler<MarkDownloadCompletedCommand>, MarkDownloadCompletedCommandHandler>();
        services.AddScoped<ICommandHandler<MarkDownloadFailedCommand>, MarkDownloadFailedCommandHandler>();
        services.AddScoped<ICommandHandler<MarkDownloadNotStartedCommand>, MarkDownloadNotStartedCommandHandler>();
        services.AddScoped<ICommandHandler<MarkImportedCommand>, MarkImportedCommandHandler>();
        services.AddScoped<ICommandHandler<MarkImportFailedCommand>, MarkImportFailedCommandHandler>();
        services.AddScoped<ICommandHandler<MarkUnitsSatisfiedCommand>, MarkUnitsSatisfiedCommandHandler>();
        services.AddScoped<ICommandHandler<CancelWorkGoalsCommand>, CancelWorkGoalsCommandHandler>();

        // Consumes Monitoring's MonitoringEnabled and Decision's ReleaseSelected → enqueue a command.
        services.AddScoped<IEventHandler<MonitoringEnabled>, MonitoringEnabledHandler>();
        services.AddScoped<IEventHandler<ReleaseSelected>, ReleaseSelectedHandler>();
        services.AddScoped<IEventHandler<MonitoringRemoved>, MonitoringRemovedHandler>();

        // Consumes Downloads' feedback (DownloadStarted/Completed/Failed) → advance the goal.
        services.AddScoped<IEventHandler<DownloadStarted>, DownloadStartedHandler>();
        services.AddScoped<IEventHandler<DownloadCompleted>, DownloadCompletedHandler>();
        services.AddScoped<IEventHandler<DownloadFailed>, DownloadFailedHandler>();
        services.AddScoped<IEventHandler<DownloadNotStarted>, DownloadNotStartedHandler>();

        // Consumes Import's feedback (ImportCompleted/ImportFailed) → meet or retry the goal.
        services.AddScoped<IEventHandler<ImportCompleted>, ImportCompletedHandler>();
        services.AddScoped<IEventHandler<ImportFailed>, ImportFailedHandler>();

        return services;
    }

    /// <summary>Applies pending migrations for the acquisition schema.</summary>
    public static async Task MigrateAcquisitionAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AcquisitionDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);
    }
}
