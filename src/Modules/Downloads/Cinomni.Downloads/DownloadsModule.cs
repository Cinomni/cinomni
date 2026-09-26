using Grpc.Net.Client;
using Cinomni.Acquisition.Contracts;
using Cinomni.Downloads.Application;
using Cinomni.Downloads.Contracts;
using Cinomni.Downloads.Engine;
using Cinomni.Downloads.EventHandlers;
using Cinomni.Downloads.Messaging;
using Cinomni.Downloads.Persistence;
using Cinomni.Downloads.Streaming;
using Cinomni.Kernel.Diagnostics;
using Cinomni.Kernel.Messaging;
using Cinomni.Kernel.Net;
using Cinomni.Operations;
using Cinomni.Torrent.Grpc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cinomni.Downloads;

/// <summary>
/// Registers the Downloads module: persisted download tasks reconciled against the libtorrent
/// sidecar over gRPC. Reacts to Acquisition's <c>DownloadQueued</c> and streams the
/// sidecar's status back into the acquisition spine (DownloadStarted/Completed/Failed). Requires the
/// platform kernel. The engine adapter is registered separately (<see cref="AddSidecarTorrentEngine"/>)
/// so tests can substitute an in-memory fake.
/// </summary>
public static class DownloadsModule
{
    private static readonly TimeSpan CheckpointInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FetchConnectTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How long startup will spend handing transfers back to the engine before serving anyway.</summary>
    private static readonly TimeSpan RecoveryBudget = TimeSpan.FromSeconds(30);

    /// <summary>Cap on a .torrent we will buffer (a hostile indexer must not exhaust memory).</summary>
    private const long MaxTorrentBytes = 8 * 1024 * 1024;

    /// <summary>
    /// Largest gRPC message either way, and the sidecar's own limit (<c>MAX_MESSAGE_BYTES</c>). The
    /// default of 4 MiB is smaller than the resume data of a large torrent — its info dictionary rides
    /// in the blob — so its checkpoint failed on every pass and it could not be recovered from one.
    /// </summary>
    private const int MaxGrpcMessageBytes = 32 * 1024 * 1024;

    /// <param name="services">The composition root's service collection.</param>
    /// <param name="configureRetention">
    /// Applied to the <see cref="DownloadRetentionOptions"/> defaults before they are validated and
    /// frozen; read at registration time because the cadence becomes a scheduled job.
    /// </param>
    /// <param name="configureTunnel">
    /// Applied to the <see cref="TunnelOptions"/> defaults. Opt-in: with no device configured — the
    /// default — the sidecar is never asked about its egress, no download is ever held, and the module
    /// behaves exactly as it does without a tunnel. Read at registration time because the poll
    /// interval becomes a scheduled-job cadence.
    /// </param>
    /// <param name="configureTransfers">
    /// Applied to the <see cref="TransferOptions"/> defaults: the stall timeout and the seeding rule
    /// new downloads are given.
    /// </param>
    public static IServiceCollection AddDownloadsModule(
        this IServiceCollection services,
        Action<DownloadRetentionOptions>? configureRetention = null,
        Action<TunnelOptions>? configureTunnel = null,
        Action<TransferOptions>? configureTransfers = null)
    {
        services.AddModuleDbContext<DownloadsDbContext>();

        var transfers = new TransferOptions();
        configureTransfers?.Invoke(transfers);
        transfers.Validate();
        services.AddSingleton(transfers);

        services.AddIntegrationEvent<DownloadStarted>(DownloadEventNames.DownloadStarted);
        services.AddIntegrationEvent<MetadataReady>(DownloadEventNames.MetadataReady);
        services.AddIntegrationEvent<DownloadCompleted>(DownloadEventNames.DownloadCompleted);
        services.AddIntegrationEvent<DownloadFailed>(DownloadEventNames.DownloadFailed);
        services.AddIntegrationEvent<DownloadNotStarted>(DownloadEventNames.DownloadNotStarted);
        services.AddIntegrationEvent<DownloadStateSaved>(DownloadEventNames.DownloadStateSaved);
        services.AddIntegrationEvent<TunnelEgressLost>(DownloadEventNames.TunnelEgressLost);
        services.AddIntegrationEvent<TunnelEgressRestored>(DownloadEventNames.TunnelEgressRestored);

        services.AddCommand<AddDownloadCommand>(DownloadCommandNames.AddDownload);
        services.AddCommand<RemoveGoalDownloadsCommand>(DownloadCommandNames.RemoveGoalDownloads);
        services.AddCommand<SaveCheckpointsCommand>(DownloadCommandNames.SaveCheckpoints);
        services.AddScheduledJob<SaveCheckpointsCommand>(
            "downloads.checkpoint", DownloadCommandNames.SaveCheckpoints, CheckpointInterval);

        var tunnel = new TunnelOptions();
        configureTunnel?.Invoke(tunnel);
        tunnel.Validate();
        services.AddSingleton(tunnel);
        services.AddScoped<TunnelWatchService>();

        // Registered unconditionally, tunnel or no tunnel. With no tunnel the cycle costs one indexed
        // key lookup and does nothing — but it is the cycle that lets go of a hold left behind by a
        // tunnel that has since been removed, which is the documented way back to a direct connection.
        // A job registered only while the guard is on would disappear exactly when it is needed, and
        // would leave an enabled row behind in the scheduler that no registration answers.
        services.AddCommand<CheckTunnelCommand>(DownloadCommandNames.CheckTunnel);
        services.AddScoped<ICommandHandler<CheckTunnelCommand>, CheckTunnelCommandHandler>();
        services.AddScheduledJob<CheckTunnelCommand>(
            "downloads.tunnel-watch", DownloadCommandNames.CheckTunnel, tunnel.PollInterval);

        var retention = new DownloadRetentionOptions();
        configureRetention?.Invoke(retention);
        retention.Validate();
        services.AddSingleton(retention);

        services.AddCommand<TrimCheckpointsCommand>(DownloadCommandNames.TrimCheckpoints);
        services.AddScheduledJob<TrimCheckpointsCommand>(
            "downloads.retention", DownloadCommandNames.TrimCheckpoints, retention.Interval);

        services.AddScoped<DownloadService>();
        services.AddScoped<IDownloadQuery, DownloadTaskQuery>();

        services.AddScoped<ICommandHandler<AddDownloadCommand>, AddDownloadCommandHandler>();
        services.AddScoped<ICommandHandler<RemoveGoalDownloadsCommand>, RemoveGoalDownloadsCommandHandler>();
        services.AddScoped<ICommandExhaustedHandler<AddDownloadCommand>, AddDownloadCommandHandler>();
        services.AddScoped<ICommandHandler<SaveCheckpointsCommand>, SaveCheckpointsCommandHandler>();
        services.AddScoped<ICommandHandler<TrimCheckpointsCommand>, TrimCheckpointsCommandHandler>();

        // Consumes Acquisition's DownloadQueued → enqueue an AddDownload command.
        services.AddScoped<IEventHandler<DownloadQueued>, DownloadQueuedHandler>();
        services.AddScoped<IEventHandler<AcquisitionCancelled>, AcquisitionCancelledHandler>();

        // Live status pump — the first hosted service outside the platform kernel.
        services.AddHostedService<DownloadStreamPump>();

        return services;
    }

    /// <summary>
    /// Registers the production gRPC engine talking to the sidecar, plus an SSRF-hardened client for
    /// fetching .torrent files from indexer links. Tests skip this and register their own
    /// <see cref="ITorrentEngine"/>.
    /// </summary>
    public static IServiceCollection AddSidecarTorrentEngine(
        this IServiceCollection services,
        Action<SidecarOptions>? configure = null)
    {
        var options = new SidecarOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);

        // Cleartext h2c, and deliberately so. The channel never leaves the host: it is a private
        // Compose network in the default topology and a loopback socket inside one network namespace
        // under the VPN overlay, so there is no path on which an observer could exist. What the
        // channel does carry is a credential (SidecarOptions.ControlToken), because the risk that is
        // real here is not eavesdropping — it is somebody else in that namespace driving the engine.
        // If this channel ever crosses a host boundary, that reasoning stops holding and TLS becomes
        // required rather than redundant.
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

        // The channel is resolvable rather than captured inside the client factory, for two reasons: it
        // is now disposed with the container instead of leaking its connection pool for the life of the
        // process, and the readiness probe can ask whether the transport comes up without issuing an
        // RPC that would change a download's state.
        //
        // No proxy, whatever the environment says. The default handler honours HTTP_PROXY and ALL_PROXY,
        // and one set for the application's outbound traffic would carry every control call — and the
        // control token on each — to the proxy instead of the sidecar next door.
        services.AddSingleton(sp => GrpcChannel.ForAddress(
            sp.GetRequiredService<SidecarOptions>().Address,
            new GrpcChannelOptions
            {
                MaxReceiveMessageSize = MaxGrpcMessageBytes,
                MaxSendMessageSize = MaxGrpcMessageBytes,
                HttpHandler = CreateSidecarHandler(),
                DisposeHttpClient = true,
            }));
        services.AddSingleton(sp => new TorrentService.TorrentServiceClient(sp.GetRequiredService<GrpcChannel>()));
        services.AddSingleton<SidecarHealthProbe>();
        services.AddSingleton<IDownloadEngineProbe>(sp => sp.GetRequiredService<SidecarHealthProbe>());

        services
            .AddHttpClient(SidecarTorrentEngine.TorrentFetchClient, client =>
            {
                client.Timeout = FetchTimeout;
                client.MaxResponseContentBufferSize = MaxTorrentBytes;
            })
            .ConfigurePrimaryHttpMessageHandler(CreateSsrfSafeHandler);

        services.AddScoped<ITorrentEngine, SidecarTorrentEngine>();
        return services;
    }

    /// <summary>
    /// Registers the qBittorrent Web API as the torrent engine instead of the sidecar. The control
    /// address is operator configuration, like the sidecar endpoint, so it is not put through the
    /// public-URL guard — that guard would refuse the localhost client this exists to reach. Torrent
    /// payloads fetched from indexers still are.
    /// </summary>
    public static IServiceCollection AddQbittorrentEngine(
        this IServiceCollection services,
        Action<QbittorrentOptions> configure)
    {
        var options = new QbittorrentOptions();
        configure(options);
        if (!Uri.TryCreate(options.BaseAddress, UriKind.Absolute, out var baseAddress)
            || baseAddress.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("Downloads:Qbittorrent:BaseAddress must be an absolute http(s) URL.");
        }

        services.AddSingleton(options);
        // One session for the process: the engine is a typed client, made per scope and per probe.
        services.AddSingleton(new QbittorrentSession());
        var root = baseAddress.ToString().TrimEnd('/');
        if (!root.EndsWith("/api/v2", StringComparison.OrdinalIgnoreCase))
        {
            root += "/api/v2";
        }

        services.AddHttpClient<QbittorrentWebEngine>(client =>
        {
            client.BaseAddress = new Uri(root + "/");
            client.Timeout = options.Timeout;
        });
        services
            .AddHttpClient(QbittorrentWebEngine.TorrentFetchClient, client =>
            {
                client.Timeout = options.Timeout;
                // The same ceiling as the sidecar's fetch: a .torrent is metadata, and an indexer that
                // answers with gigabytes would otherwise be read whole into memory.
                client.MaxResponseContentBufferSize = MaxTorrentBytes;
            })
            .ConfigurePrimaryHttpMessageHandler(CreateSsrfSafeHandler);
        services.AddScoped<ITorrentEngine>(sp => sp.GetRequiredService<QbittorrentWebEngine>());
        // Without it the readiness check found no sidecar probe and reported a client it never asked as healthy.
        services.AddTransient<IDownloadEngineProbe>(sp => sp.GetRequiredService<QbittorrentWebEngine>());
        return services;
    }

    /// <summary>Applies pending migrations for the downloads schema (idempotent).</summary>
    public static async Task MigrateDownloadsAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DownloadsDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);
    }

    /// <summary>
    /// Startup recovery: re-establishes every in-flight transfer with the engine from its persisted
    /// resume-data checkpoint (see <see cref="DownloadService.RecoverAsync"/>). Idempotent.
    /// <para>
    /// Deliberately unable to stop the installation from starting. The sidecar is a separate process
    /// and is routinely absent — not yet started beside the backend, deliberately stopped,
    /// or unreachable during a tunnel outage — and none of those is a reason to refuse to serve the
    /// library, the catalogue or playback. A transfer that could not be handed back is left exactly
    /// as it was and is tried again on the next start.
    /// </para>
    /// <para>
    /// Bounded on purpose too: the pass is one out-of-process round trip per in-flight transfer, and
    /// startup must not grow with the size of the queue behind an engine that answers slowly.
    /// Whatever is not re-established inside <see cref="RecoveryBudget"/> waits for the next start.
    /// </para>
    /// <para>
    /// An installation with a tunnel configured gets <b>one egress observation first</b>. Recovery
    /// requires a positive, recent verification, and this pass runs before any hosted service — so
    /// without this call the only verdict on record would be the previous process's, or none at all,
    /// and either would mean either re-arming every torrent on trust or refusing to ever recover.
    /// </para>
    /// </summary>
    public static async Task RecoverDownloadsAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DownloadsModule));

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(RecoveryBudget);

        try
        {
            await ObserveEgressAsync(services, logger, budget.Token);

            await using var scope = services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<DownloadService>().RecoverAsync(budget.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "Download recovery ran out of its {Budget} budget; the transfers it did not reach are "
                + "unchanged and will be re-established on the next start.",
                RecoveryBudget);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Download recovery failed; in-flight transfers were left untouched.");
        }
    }

    /// <summary>
    /// Takes one egress observation before recovery decides anything, on an installation that
    /// configured a tunnel. Its own scope: the watch tracks the state row and the held tasks, and
    /// recovery must read the committed result rather than another service's change tracker.
    /// <para>
    /// A failure here is not escalated. The observation exists to make the verdict recovery reads
    /// <b>fresh</b>; one that could not be taken leaves the verdict stale, which recovery already
    /// treats as a refusal. Failing closed is the outcome either way.
    /// </para>
    /// </summary>
    private static async Task ObserveEgressAsync(
        IServiceProvider services,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (!services.GetRequiredService<TunnelOptions>().IsConfigured)
        {
            return;
        }

        try
        {
            await using var scope = services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<TunnelWatchService>().CheckAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex,
                "The egress observation before download recovery could not be taken; recovery will refuse "
                + "to hand any transfer back until one verifies.");
        }
    }

    // The handler itself lives in the Kernel: it is the same transport Discovery, Metadata, Subtitles
    // and Notifications use, and one security surface beats five copies of it.
    /// <summary>The control channel's transport: direct, never through a proxy.</summary>
    internal static SocketsHttpHandler CreateSidecarHandler() => new()
    {
        UseProxy = false,
        Proxy = null,
        // What GrpcChannel configures on its own handler: one HTTP/2 connection would otherwise cap the
        // streams in flight, and the status pump holds several open at once.
        EnableMultipleHttp2Connections = true,
    };

    private static SocketsHttpHandler CreateSsrfSafeHandler() =>
        SsrfSafeHttpHandler.Create(FetchConnectTimeout, CinomniTelemetry.Modules.Downloads);
}
