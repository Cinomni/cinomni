using Cinomni.Catalog.Contracts;
using Cinomni.Decision.Contracts;
using Cinomni.Import.Contracts;
using Cinomni.Kernel.Messaging;
using Cinomni.Monitoring.Application;
using Cinomni.Monitoring.Contracts;
using Cinomni.Monitoring.EventHandlers;
using Cinomni.Monitoring.Messaging;
using Cinomni.Monitoring.Persistence;
using Cinomni.Operations;
using Cinomni.Operations.Settings;
using Cinomni.Search.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Monitoring;

/// <summary>
/// Registers the Monitoring module: what content is watched, and the search cadence that
/// drives Discovery. Requires the platform kernel (<c>AddOperations</c>) first — it shares its
/// connection and unit of work for atomic events, and reacts to Catalog's <c>WorkAdded</c> and
/// <c>SeriesStructureChanged</c> and to Import's <c>MediaAvailable</c>.
/// </summary>
public static class MonitoringModule
{
    /// <summary>How often the missing-search sweep runs.</summary>
    public static readonly TimeSpan EvaluateMissingInterval = TimeSpan.FromMinutes(15);

    public static IServiceCollection AddMonitoringModule(this IServiceCollection services)
    {
        services.AddModuleDbContext<MonitoringDbContext>();

        MonitoringSweepOptions BindSweep(SettingsView view) => new()
        {
            SearchDelay = view.GetTimeSpan(MonitoringSweepSettingDefinitions.SearchDelay) ?? TimeSpan.Zero,
        };
        services.AddSettingsCheck(
            [MonitoringSweepSettingDefinitions.SearchDelay], BindSweep, MonitoringSweepOptions.Check);
        services.AddLiveOptions(BindSweep);

        services.AddIntegrationEvent<MonitoringEnabled>(MonitoringEventNames.MonitoringEnabled);
        services.AddIntegrationEvent<SearchRequested>(MonitoringEventNames.SearchRequested);
        services.AddIntegrationEvent<MonitoringRemoved>(MonitoringEventNames.MonitoringRemoved);

        services.AddCommand<ApplyMonitoringPolicyCommand>(MonitoringCommandNames.ApplyPolicy);
        services.AddCommand<EvaluateMissingCommand>(MonitoringCommandNames.EvaluateMissing);
        services.AddCommand<SyncSeriesTargetsCommand>(MonitoringCommandNames.SyncSeriesTargets);
        services.AddCommand<MarkUnitsSatisfiedCommand>(MonitoringCommandNames.MarkUnitsSatisfied);
        services.AddCommand<SetUpgradeWantedCommand>(MonitoringCommandNames.SetUpgradeWanted);
        services.AddCommand<RemoveWorkTargetsCommand>(MonitoringCommandNames.RemoveWorkTargets);

        services.AddScoped<SeriesTargetMaterializer>();
        // Registered as itself too: the WorkAdded-driven default uses ApplyInitialPolicyAsync, which is
        // this module's own and not part of the published contract.
        services.AddScoped<MonitoringCommands>();
        services.AddScoped<IMonitoringCommands>(sp => sp.GetRequiredService<MonitoringCommands>());
        services.AddScoped<IMonitoringQuery, MonitoringQuery>();
        services.AddScoped<MonitoringBrowse>();
        services.AddScoped<CalendarReader>();
        services.AddScoped<ITargetSearchPlans, TargetSearchPlanner>();

        services.AddScoped<ICommandHandler<ApplyMonitoringPolicyCommand>, ApplyMonitoringPolicyCommandHandler>();
        services.AddScoped<ICommandHandler<EvaluateMissingCommand>, EvaluateMissingCommandHandler>();
        services.AddScoped<ICommandHandler<SyncSeriesTargetsCommand>, SyncSeriesTargetsCommandHandler>();
        services.AddScoped<ICommandHandler<MarkUnitsSatisfiedCommand>, MarkUnitsSatisfiedCommandHandler>();
        services.AddScoped<ICommandHandler<SetUpgradeWantedCommand>, SetUpgradeWantedCommandHandler>();
        services.AddScoped<ICommandHandler<RemoveWorkTargetsCommand>, RemoveWorkTargetsCommandHandler>();

        // A catalogued work becomes a monitored target; its structure becomes season/episode targets.
        services.AddScoped<IEventHandler<WorkAdded>, WorkAddedHandler>();
        services.AddScoped<IEventHandler<WorkRemoved>, WorkRemovedHandler>();
        services.AddScoped<IEventHandler<SeriesStructureChanged>, SeriesStructureChangedHandler>();

        // An imported asset stops the targets it satisfies from being searched forever.
        services.AddScoped<IEventHandler<MediaAvailable>, MediaAvailableHandler>();

        // Consumes Decision's UpgradeAssessed → records on each target whether it is worth bettering.
        services.AddScoped<IEventHandler<UpgradeAssessed>, UpgradeAssessedHandler>();

        // Drive the missing-search cadence from the platform scheduler.
        services.AddScheduledJob<EvaluateMissingCommand>(
            "monitoring.evaluate-missing", MonitoringCommandNames.EvaluateMissing, EvaluateMissingInterval);

        return services;
    }

    /// <summary>Applies pending migrations for the monitoring schema (idempotent).</summary>
    public static async Task MigrateMonitoringAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MonitoringDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);
    }
}
