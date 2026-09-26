using Cinomni.Acquisition;
using Cinomni.Catalog;
using Cinomni.Decision;
using Cinomni.Discovery;
using Cinomni.Downloads;
using Cinomni.Host.Health;
using Cinomni.Host.Observability;
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
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Host.Tests;

/// <summary>
/// The module registrations the Host composes, in one place so the test that reads routing metadata and
/// the test that sends real requests cannot drift apart. Nothing here connects: every registration is
/// lazy and the connection string is deliberately unusable, so a test that touches the database fails
/// loudly instead of reaching a real one.
/// </summary>
internal static class HostComposition
{
    /// <summary>A syntactically valid connection string that must never open.</summary>
    internal const string UnusableConnectionString =
        "Host=localhost;Database=cinomni_unused;Username=none;Password=none";

    internal static IServiceCollection AddCinomniModules(this IServiceCollection services)
    {
        services.AddOperations(UnusableConnectionString);
        services.AddIdentityModule();
        services.AddIdentityAuthentication();
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
        services.AddPlaybackModule();
        services.AddPlaybackAdapters();
        services.AddSubtitlesModule();
        services.AddRequestsModule();
        services.AddNotificationsModule();
        services.AddRealTimeModule();

        // The probes are part of the surface CinomniApi maps, so a composition without them cannot map
        // it. Registered with the shipped defaults; nothing here runs a check — these tests read routing
        // metadata and never open a connection.
        services.AddCinomniHealthChecks(new ObservabilityOptions());
        return services;
    }
}
