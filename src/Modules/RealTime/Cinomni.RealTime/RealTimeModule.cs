using Cinomni.Acquisition.Contracts;
using Cinomni.Downloads.Contracts;
using Cinomni.Import.Contracts;
using Cinomni.Kernel.Messaging;
using Cinomni.Notifications.Contracts;
using Cinomni.RealTime.EventHandlers;
using Cinomni.RealTime.Streaming;
using Cinomni.Requests.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.RealTime;

/// <summary>
/// Registers the RealTime module: one authenticated Server-Sent Events stream that tells a browser what
/// changed, so the SPA stops asking. It owns <b>no schema and no state</b> — nothing it handles is
/// persisted, because a signal is only worth delivering to a connection that is open, and a client that
/// reconnects re-reads the REST surface, which is the authority in every case.
/// <para>
/// It is a pure consumer: it publishes no integration event and no other module references it. That is
/// what lets it depend on several modules' contracts at once without becoming a hub in the dependency
/// graph — every arrow points into it.
/// </para>
/// </summary>
public static class RealTimeModule
{
    public static IServiceCollection AddRealTimeModule(this IServiceCollection services)
    {
        // Singleton: a connection outlives the request scope that opened it.
        services.AddSingleton<RealtimeHub>();
        services.AddHostedService<DownloadProgressPublisher>();

        // One class handles several events, so it is registered once and surfaced under each interface
        // the dispatcher will look up.
        services.AddScoped<DownloadSignals>();
        services.AddScoped<IEventHandler<DownloadStarted>>(sp => sp.GetRequiredService<DownloadSignals>());
        services.AddScoped<IEventHandler<MetadataReady>>(sp => sp.GetRequiredService<DownloadSignals>());
        services.AddScoped<IEventHandler<DownloadCompleted>>(sp => sp.GetRequiredService<DownloadSignals>());
        services.AddScoped<IEventHandler<DownloadFailed>>(sp => sp.GetRequiredService<DownloadSignals>());
        services.AddScoped<IEventHandler<TunnelEgressLost>>(sp => sp.GetRequiredService<DownloadSignals>());
        services.AddScoped<IEventHandler<TunnelEgressRestored>>(sp => sp.GetRequiredService<DownloadSignals>());

        services.AddScoped<AcquisitionSignals>();
        services.AddScoped<IEventHandler<AcquisitionRequested>>(sp => sp.GetRequiredService<AcquisitionSignals>());
        services.AddScoped<IEventHandler<CandidateSelected>>(sp => sp.GetRequiredService<AcquisitionSignals>());
        services.AddScoped<IEventHandler<DownloadQueued>>(sp => sp.GetRequiredService<AcquisitionSignals>());
        services.AddScoped<IEventHandler<AcquisitionSucceeded>>(sp => sp.GetRequiredService<AcquisitionSignals>());
        services.AddScoped<IEventHandler<AcquisitionFailed>>(sp => sp.GetRequiredService<AcquisitionSignals>());
        services.AddScoped<IEventHandler<AcquisitionRetrying>>(sp => sp.GetRequiredService<AcquisitionSignals>());

        services.AddScoped<IEventHandler<MediaAvailable>, LibrarySignals>();
        services.AddScoped<IEventHandler<NotificationRaised>, NotificationSignals>();

        services.AddScoped<RequestSignals>();
        services.AddScoped<IEventHandler<MediaRequested>>(sp => sp.GetRequiredService<RequestSignals>());
        services.AddScoped<IEventHandler<MediaRequestApproved>>(sp => sp.GetRequiredService<RequestSignals>());
        services.AddScoped<IEventHandler<MediaRequestRejected>>(sp => sp.GetRequiredService<RequestSignals>());

        return services;
    }
}
