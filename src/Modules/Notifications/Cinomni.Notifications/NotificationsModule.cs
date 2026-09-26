using Cinomni.Acquisition.Contracts;
using Cinomni.Downloads.Contracts;
using Cinomni.Import.Contracts;
using Cinomni.Kernel.Diagnostics;
using Cinomni.Kernel.Messaging;
using Cinomni.Kernel.Net;
using Cinomni.Metadata.Contracts;
using Cinomni.Notifications.Application;
using Cinomni.Notifications.Contracts;
using Cinomni.Notifications.Delivery;
using Cinomni.Notifications.EventHandlers;
using Cinomni.Notifications.Messaging;
using Cinomni.Notifications.Persistence;
using Cinomni.Operations;
using Cinomni.Requests.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Notifications;

/// <summary>
/// Registers the Notifications module: reacts to the domain events worth surfacing (Import's
/// <c>MediaAvailable</c>, Acquisition's <c>AcquisitionFailed</c>, Metadata's <c>ProviderDegraded</c>,
/// Requests' <c>MediaRequested</c>),
/// composes an in-app notification (resolving the work title from Catalog, →i) and fans it out to the
/// configured outbound channels. Terminal on the acquisition spine: the one event it emits
/// (<c>NotificationRaised</c>) says an inbox changed and feeds nothing back into acquisition. Requires
/// the platform kernel. The delivery adapter is registered separately
/// (<see cref="AddNotificationAdapters"/>) so tests can substitute a fake.
/// </summary>
public static class NotificationsModule
{
    private static readonly TimeSpan DeliveryTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DeliveryConnectTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Cap on a webhook response we will buffer (a hostile endpoint must not exhaust memory).</summary>
    private const long MaxResponseBytes = 1 * 1024 * 1024;

    /// <param name="services">The composition root's service collection.</param>
    /// <param name="configureRetention">
    /// Applied to the <see cref="NotificationRetentionOptions"/> defaults before they are validated
    /// and frozen; read at registration time because the cadence becomes a scheduled job.
    /// </param>
    public static IServiceCollection AddNotificationsModule(
        this IServiceCollection services,
        Action<NotificationRetentionOptions>? configureRetention = null)
    {
        services.AddModuleDbContext<NotificationsDbContext>();

        services.AddIntegrationEvent<NotificationRaised>(NotificationEventNames.NotificationRaised);

        services.AddCommand<RaiseNotificationCommand>(NotificationCommandNames.RaiseNotification);

        var retention = new NotificationRetentionOptions();
        configureRetention?.Invoke(retention);
        retention.Validate();
        services.AddSingleton(retention);

        services.AddCommand<PurgeNotificationsCommand>(NotificationCommandNames.PurgeNotifications);
        services.AddScoped<ICommandHandler<PurgeNotificationsCommand>, PurgeNotificationsCommandHandler>();
        services.AddScheduledJob<PurgeNotificationsCommand>(
            "notifications.retention", NotificationCommandNames.PurgeNotifications, retention.Interval);

        // One inbox instance per scope serves both the read model and the mark-read actions.
        services.AddScoped<NotificationInboxService>();
        services.AddScoped<INotificationQuery>(sp => sp.GetRequiredService<NotificationInboxService>());
        services.AddScoped<INotificationInbox>(sp => sp.GetRequiredService<NotificationInboxService>());
        services.AddScoped<INotificationChannels, ChannelService>();

        services.AddScoped<ICommandHandler<RaiseNotificationCommand>, RaiseNotificationCommandHandler>();

        // Consumes the events worth notifying on → enqueue a RaiseNotification command.
        services.AddScoped<IEventHandler<MediaAvailable>, MediaAvailableHandler>();
        services.AddScoped<IEventHandler<AcquisitionFailed>, AcquisitionFailedHandler>();
        services.AddScoped<IEventHandler<ProviderDegraded>, ProviderDegradedHandler>();
        services.AddScoped<IEventHandler<MediaRequested>, MediaRequestedHandler>();
        services.AddScoped<IEventHandler<TunnelEgressLost>, TunnelEgressLostHandler>();
        services.AddScoped<IEventHandler<TunnelEgressRestored>, TunnelEgressRestoredHandler>();

        return services;
    }

    /// <summary>
    /// Registers the production webhook dispatcher over an SSRF-hardened client. Tests skip this and
    /// register their own <see cref="INotificationDispatcher"/> fake.
    /// </summary>
    public static IServiceCollection AddNotificationAdapters(this IServiceCollection services)
    {
        services
            .AddHttpClient<INotificationDispatcher, WebhookNotificationDispatcher>(client =>
            {
                client.Timeout = DeliveryTimeout;
                client.MaxResponseContentBufferSize = MaxResponseBytes;
                client.DefaultRequestHeaders.Add("User-Agent", "Cinomni/1.0");
            })
            .ConfigurePrimaryHttpMessageHandler(CreateSsrfSafeHandler);

        return services;
    }

    /// <summary>Applies pending migrations for the notifications schema (idempotent).</summary>
    public static async Task MigrateNotificationsAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);
    }

    // The handler itself lives in the Kernel: it is the same transport Discovery, Metadata, Subtitles
    // and Downloads use, and one security surface beats five copies of it. A webhook target is
    // user-supplied, so this is the boundary that keeps a delivery channel from probing the LAN.
    private static SocketsHttpHandler CreateSsrfSafeHandler() =>
        SsrfSafeHttpHandler.Create(DeliveryConnectTimeout, CinomniTelemetry.Modules.Notifications);
}
