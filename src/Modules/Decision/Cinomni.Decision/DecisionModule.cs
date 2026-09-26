using Cinomni.Acquisition.Contracts;
using Cinomni.Decision.Application;
using Cinomni.Decision.Contracts;
using Cinomni.Decision.EventHandlers;
using Cinomni.Decision.Evaluation;
using Cinomni.Decision.Messaging;
using Cinomni.Decision.Persistence;
using Cinomni.Discovery.Contracts;
using Cinomni.Kernel.Messaging;
using Cinomni.Library.Contracts;
using Cinomni.Operations;
using Cinomni.Operations.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Decision;

/// <summary>
/// Registers the Decision module: evaluate parsed releases against profiles, explain the
/// verdict, and select the best candidate. Reacts to Discovery's <c>SearchCompleted</c>, and calls
/// Release Parsing and Discovery's result read-interface by interface. Requires the platform kernel.
/// </summary>
public static class DecisionModule
{
    /// <param name="services">The composition root's service collection.</param>
    /// <param name="configureRetention">
    /// Applied to the <see cref="DecisionRetentionOptions"/> defaults before they are validated and
    /// frozen; read at registration time because the cadence becomes a scheduled job.
    /// </param>
    public static IServiceCollection AddDecisionModule(
        this IServiceCollection services,
        Action<DecisionRetentionOptions>? configureRetention = null)
    {
        services.AddModuleDbContext<DecisionDbContext>();

        services.AddIntegrationEvent<ReleaseEvaluated>(DecisionEventNames.ReleaseEvaluated);
        services.AddIntegrationEvent<ReleaseSelected>(DecisionEventNames.ReleaseSelected);
        services.AddIntegrationEvent<NoAcceptableRelease>(DecisionEventNames.NoAcceptableRelease);
        services.AddIntegrationEvent<UpgradeAssessed>(DecisionEventNames.UpgradeAssessed);

        services.AddCommand<EvaluateReleasesCommand>(DecisionCommandNames.EvaluateReleases);
        services.AddCommand<AssessUpgradeCommand>(DecisionCommandNames.AssessUpgrade);
        services.AddCommand<ExcludeReleaseForTargetCommand>(DecisionCommandNames.ExcludeReleaseForTarget);

        var retention = new DecisionRetentionOptions();
        configureRetention?.Invoke(retention);
        retention.Validate();
        services.AddSingleton(retention);

        // The settings store's catalogue for DecisionRetentionOptions: both of its properties already
        // carry a settings key and a Check() rule. The purge cadence itself (Interval) is captured once
        // by AddScheduledJob below and only picks up a stored change after a restart.
        DecisionRetentionOptions BindRetention(SettingsView view) => new()
        {
            EvaluationRetention =
                view.GetTimeSpan(DecisionRetentionSettingDefinitions.EvaluationRetention) ?? retention.EvaluationRetention,
            Interval = view.GetTimeSpan(DecisionRetentionSettingDefinitions.Interval) ?? retention.Interval,
        };

        services.AddSettingsCheck(DecisionRetentionSettingDefinitions.All, BindRetention, DecisionRetentionOptions.Check);
        services.AddLiveOptions(BindRetention);

        services.AddCommand<PurgeEvaluationsCommand>(DecisionCommandNames.PurgeEvaluations);
        services.AddScoped<ICommandHandler<PurgeEvaluationsCommand>, PurgeEvaluationsCommandHandler>();
        services.AddScheduledJob<PurgeEvaluationsCommand>(
            "decision.retention", DecisionCommandNames.PurgeEvaluations, retention.Interval);

        services.AddScoped<DecisionEngine>();
        services.AddScoped<CurrentQualityResolver>();
        services.AddScoped<UpgradeAssessment>();
        services.AddScoped<IProfileQuery, ProfileQuery>();
        services.AddScoped<IProfileAdministration, ProfileAdministration>();
        services.AddScoped<IReleaseEvaluationQuery, ReleaseEvaluationQuery>();
        services.AddScoped<IInteractiveSearch, InteractiveSearchService>();
        services.AddScoped<IReleaseBlocklist, ReleaseBlocklist>();

        services.AddScoped<ICommandHandler<EvaluateReleasesCommand>, EvaluateReleasesCommandHandler>();
        services.AddScoped<ICommandHandler<AssessUpgradeCommand>, AssessUpgradeCommandHandler>();
        services.AddScoped<ICommandHandler<ExcludeReleaseForTargetCommand>, ExcludeReleaseForTargetCommandHandler>();

        // Consumes Discovery's SearchCompleted → enqueues an evaluation.
        services.AddScoped<IEventHandler<SearchCompleted>, SearchCompletedHandler>();

        // Consumes Library's MediaAssetRegistered → enqueues the cutoff assessment for what landed.
        services.AddScoped<IEventHandler<MediaAssetRegistered>, MediaAssetRegisteredHandler>();

        // Consumes Acquisition's AcquisitionAttemptFailed → stops offering that release to that goal.
        services.AddScoped<IEventHandler<AcquisitionAttemptFailed>, AcquisitionAttemptFailedHandler>();

        return services;
    }

    /// <summary>
    /// Applies pending migrations and seeds one default profile <b>per content kind</b> (idempotent).
    /// </summary>
    /// <remarks>
    /// The guard is per scope, not "is the table empty". An existing install already holds the movie
    /// profile, so an emptiness check would seed nothing and leave series content judged by a movie's
    /// size bounds — the failure mode is a perfectly explainable but entirely wrong rejection.
    /// </remarks>
    public static async Task MigrateDecisionAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DecisionDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);

        var seeded = false;
        seeded |= await SeedIfMissingAsync(dbContext, ProfileScope.Movie, DefaultProfile.CreateMovieDefault, cancellationToken);
        seeded |= await SeedIfMissingAsync(dbContext, ProfileScope.Series, DefaultProfile.CreateSeriesDefault, cancellationToken);

        if (seeded)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private static async Task<bool> SeedIfMissingAsync(
        DecisionDbContext dbContext,
        string appliesTo,
        Func<AcquisitionProfile> create,
        CancellationToken cancellationToken)
    {
        if (await dbContext.Profiles.AnyAsync(p => p.AppliesTo == appliesTo, cancellationToken))
        {
            return false;
        }

        dbContext.Profiles.Add(create());
        return true;
    }
}
