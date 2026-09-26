using Cinomni.Acquisition;
using Cinomni.Catalog;
using Cinomni.Decision;
using Cinomni.Discovery;
using Cinomni.Downloads;
using Cinomni.Identity;
using Cinomni.Import;
using Cinomni.Library;
using Cinomni.Metadata;
using Cinomni.Monitoring;
using Cinomni.Notifications;
using Cinomni.Operations;
using Cinomni.Playback;
using Cinomni.RealTime;
using Cinomni.ReleaseParsing;
using Cinomni.Requests;
using Cinomni.Subtitles;

namespace Cinomni.Host;

/// <summary>
/// The composition root's module list: the platform kernel first, then every module registering
/// itself. It is a method rather than inline statements in <c>Program</c> so a test can compose the
/// real installation instead of a copy that drifts — a module added here without its retention
/// callback has to be visible to whatever asserts the wiring.
/// <para>
/// Registration only. Nothing connects, nothing migrates: the Host applies migrations and runs
/// recovery after the provider is built, and a test that only reads registrations never touches
/// PostgreSQL.
/// </para>
/// </summary>
internal static class CinomniModules
{
    /// <param name="services">The composition root's service collection.</param>
    /// <param name="configuration">Configuration for the sections each module binds explicitly.</param>
    /// <param name="connectionString">The single PostgreSQL connection every module schema shares.</param>
    public static IServiceCollection AddCinomniModules(
        this IServiceCollection services,
        IConfiguration configuration,
        string connectionString)
    {
        // Retention windows come from one configuration section; each owner validates its own values
        // at registration, so an inconsistent policy stops startup here rather than deleting the wrong
        // rows weeks later. See RetentionConfiguration for the full list, including what is never purged.
        var retention = new RetentionConfiguration(configuration);

        services.AddOperations(connectionString, retention.Operations, configuration);

        // No client logs its requests. The factory's default handlers write every request URI at
        // Information, and here a URI is a credential more often than not: a Discord webhook carries its
        // token in the path, an indexer its API key in the query. Spans are redacted separately
        // (TelemetryRedaction); what a module needs to say about a call, it logs itself, without the URL.
        services.ConfigureHttpClientDefaults(client => client.RemoveAllLoggers());

        // Backup sits on the kernel and covers every schema below, so it is registered here rather than
        // by a module. It has no HTTP surface by design — see BackupRegistration for why a dump must not
        // be reachable over the network.
        services.AddConfiguredBackup(configuration, connectionString);

        services.AddIdentityModule(retention.Identity);
        services.AddIdentityAuthentication();
        services.AddCatalogModule();
        services.AddMetadataModule();
        services.AddConfiguredMetadataAdapters(configuration, retention);
        services.AddMonitoringModule();
        services.AddDiscoveryModule(retention.Discovery);
        services.AddIndexerClients();
        services.AddReleaseParsingModule(retention.ReleaseParsing);
        services.AddDecisionModule(retention.Decision);
        services.AddAcquisitionModule();
        services.AddDownloadsModule(
            retention.Downloads,
            options => ModuleConfiguration.ApplyTunnelGuard(configuration, options),
            options => ModuleConfiguration.ApplyTransfers(configuration, options));
        services.AddConfiguredDownloadsAdapters(configuration);
        services.AddImportModule();
        services.AddConfiguredImportAdapters(configuration);
        services.AddLibraryModule();
        services.AddPlaybackModule();
        services.AddConfiguredPlaybackAdapters(configuration, retention.Playback);
        services.AddSubtitlesModule();
        services.AddConfiguredSubtitleAdapters(configuration);
        services.AddRequestsModule();
        services.AddNotificationsModule(retention.Notifications);
        services.AddNotificationAdapters();
        services.AddRealTimeModule();

        return services;
    }
}
