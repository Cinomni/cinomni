using Cinomni.Kernel.Messaging;
using Cinomni.ReleaseParsing.Application;
using Cinomni.ReleaseParsing.Contracts;
using Cinomni.ReleaseParsing.Messaging;
using Cinomni.ReleaseParsing.Parsing;
using Cinomni.ReleaseParsing.Persistence;
using Cinomni.Operations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.ReleaseParsing;

/// <summary>
/// Registers the Release Parsing module: the pure release-name parser plus its audit trail.
/// It knows neither Discovery nor Decision — Decision calls <see cref="IReleaseParser"/> directly.
/// <see cref="ParseAndPersistCommand"/> is the audit trail's way in and is registered, but nothing
/// enqueues it yet: whether every evaluated candidate should leave a row is a product decision that
/// has not been taken. Requires the platform kernel (<c>AddOperations</c>).
/// </summary>
public static class ReleaseParsingModule
{
    /// <summary>What <see cref="ReleaseParser.Version"/> changed, recorded next to the seeded version row.</summary>
    private const string ActiveRuleSetNotes =
        "Series episode numbering: season/episode, multi-episode, season packs, absolute (anime) "
        + "and date-based releases, with the numbering folded into the canonical key. Releases "
        + "parsed under 1.0.0 keep their original key and are not re-parsed.";

    /// <param name="services">The composition root's service collection.</param>
    /// <param name="configureRetention">
    /// Applied to the <see cref="ReleaseParsingRetentionOptions"/> defaults before they are validated
    /// and frozen; read at registration time because the cadence becomes a scheduled job.
    /// </param>
    public static IServiceCollection AddReleaseParsingModule(
        this IServiceCollection services,
        Action<ReleaseParsingRetentionOptions>? configureRetention = null)
    {
        services.AddModuleDbContext<ReleaseParsingDbContext>();

        services.AddIntegrationEvent<ReleaseParsed>(ReleaseParsingEventNames.ReleaseParsed);
        services.AddCommand<ParseAndPersistCommand>(ReleaseParsingCommandNames.ParseAndPersist);

        var retention = new ReleaseParsingRetentionOptions();
        configureRetention?.Invoke(retention);
        retention.Validate();
        services.AddSingleton(retention);

        services.AddCommand<PurgeParsedReleasesCommand>(ReleaseParsingCommandNames.PurgeParsedReleases);
        services.AddScoped<ICommandHandler<PurgeParsedReleasesCommand>, PurgeParsedReleasesCommandHandler>();
        services.AddScheduledJob<PurgeParsedReleasesCommand>(
            "parsing.retention", ReleaseParsingCommandNames.PurgeParsedReleases, retention.Interval);

        // Both parsers are pure and stateless (only compiled regexes) — singletons. The numbering
        // parser is exposed separately so Import can resolve media file names through the very same
        // detectors instead of growing a second set of episode regexes.
        services.AddSingleton<IReleaseParser, ReleaseParser>();
        services.AddSingleton<IEpisodeNumberParser, EpisodeNumberParser>();
        services.AddScoped<ICommandHandler<ParseAndPersistCommand>, ParseAndPersistCommandHandler>();

        return services;
    }

    /// <summary>
    /// Applies pending migrations and seeds the active parser rule-set version (idempotent).
    /// A version bump therefore adds a <b>second</b> rule-version row rather than replacing the
    /// first: the older row is the audit boundary for every release parsed before the bump, which
    /// keeps its original canonical key (no re-parse job exists, by design).
    /// </summary>
    public static async Task MigrateReleaseParsingAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ReleaseParsingDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);

        var alreadySeeded = await dbContext.ParseRuleVersions
            .AnyAsync(v => v.Version == ReleaseParser.Version, cancellationToken);
        if (!alreadySeeded)
        {
            dbContext.ParseRuleVersions.Add(new ParseRuleVersion
            {
                Version = ReleaseParser.Version,
                Notes = ActiveRuleSetNotes,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }
}
