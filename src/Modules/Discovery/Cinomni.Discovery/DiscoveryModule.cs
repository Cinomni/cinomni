using Cinomni.Discovery.Application;
using Cinomni.Discovery.Contracts;
using Cinomni.Discovery.EventHandlers;
using Cinomni.Discovery.Indexers;
using Cinomni.Discovery.Indexers.Definition;
using Cinomni.Discovery.Messaging;
using Cinomni.Discovery.Persistence;
using Cinomni.Kernel.Diagnostics;
using Cinomni.Kernel.Messaging;
using Cinomni.Kernel.Net;
using Cinomni.Operations;
using Cinomni.Search.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cinomni.Discovery;

/// <summary>
/// Registers the Discovery module: federated indexer search feeding the acquisition spine.
/// Requires the platform kernel (<c>AddOperations</c>) first — it shares its connection and unit
/// of work, and reacts to Monitoring's <c>SearchRequested</c>.
/// </summary>
public static class DiscoveryModule
{
    private static readonly TimeSpan IndexerRequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan IndexerConnectTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Cap on an indexer response we will buffer (a hostile indexer must not exhaust memory).</summary>
    private const long MaxResponseBytes = 8 * 1024 * 1024;

    /// <param name="services">The composition root's service collection.</param>
    /// <param name="configureRetention">
    /// Applied to the <see cref="SearchRetentionOptions"/> defaults before they are validated and
    /// frozen; read at registration time because the window's cadence becomes a scheduled job.
    /// </param>
    public static IServiceCollection AddDiscoveryModule(
        this IServiceCollection services,
        Action<SearchRetentionOptions>? configureRetention = null)
    {
        services.AddModuleDbContext<DiscoveryDbContext>();

        services.AddIntegrationEvent<SearchCompleted>(DiscoveryEventNames.SearchCompleted);
        services.AddCommand<ExecuteSearchCommand>(DiscoveryCommandNames.ExecuteSearch);

        var retention = new SearchRetentionOptions();
        configureRetention?.Invoke(retention);
        retention.Validate();
        services.AddSingleton(retention);

        services.AddCommand<PurgeSearchesCommand>(DiscoveryCommandNames.PurgeSearches);
        services.AddScoped<ICommandHandler<PurgeSearchesCommand>, PurgeSearchesCommandHandler>();
        services.AddScheduledJob<PurgeSearchesCommand>(
            "discovery.retention", DiscoveryCommandNames.PurgeSearches, retention.Interval);

        // ReleaseSearch serves both public read/search interfaces; share one instance per scope.
        services.AddScoped<ReleaseSearch>();
        services.AddScoped<IReleaseSearch>(sp => sp.GetRequiredService<ReleaseSearch>());
        services.AddScoped<IReleaseSearchResults>(sp => sp.GetRequiredService<ReleaseSearch>());
        // Stateless over the platform's cipher singleton, so one instance serves every scope. Lives
        // here rather than in AddIndexerClients because the administration surface needs it to write
        // a credential even in a composition that registers no transport at all.
        services.AddSingleton<IndexerCredentialProtector>();
        services.AddScoped<IIndexerAdministration, IndexerAdministration>();
        services.AddScoped<IIndexerQuota, IndexerQuota>();

        // Catalog sources live here, not in AddIndexerClients: subscribing to one is administration,
        // and a composition with no indexer transport still manages its catalog. The manifest fetch
        // is third-party HTTP, so it rides the Kernel's SSRF-safe handler like every indexer request.
        services
            .AddHttpClient(HttpIndexerCatalogFetcher.HttpClientName, client =>
            {
                client.Timeout = HttpIndexerCatalogFetcher.RequestTimeout;
                client.MaxResponseContentBufferSize = HttpIndexerCatalogFetcher.MaxManifestBytes;
            })
            .ConfigurePrimaryHttpMessageHandler(() => SsrfSafeHttpHandler.Create(
                HttpIndexerCatalogFetcher.ConnectTimeout, CinomniTelemetry.Modules.Discovery));
        services.AddScoped<IIndexerCatalogFetcher, HttpIndexerCatalogFetcher>();
        services.AddScoped<IIndexerCatalogSources, IndexerCatalogSources>();

        services.AddScoped<ICommandHandler<ExecuteSearchCommand>, ExecuteSearchCommandHandler>();

        // Consumes Monitoring's SearchRequested (registered as an integration event by Monitoring).
        services.AddScoped<IEventHandler<SearchRequested>, SearchRequestedHandler>();

        return services;
    }

    /// <summary>
    /// Registers every production indexer adapter on an SSRF-hardened HttpClient each — refusing a
    /// non-public address (checked against the resolved IP, anti-rebinding), never following a
    /// redirect, bounding time and response size — and the single <see cref="IIndexerClient"/>
    /// (<see cref="IndexerClientFactory"/>) that dispatches to the right one by protocol, which is
    /// what <c>ReleaseSearch</c> actually depends on. Tests substitute their own
    /// <see cref="IIndexerClient"/> entirely and skip this.
    /// </summary>
    public static IServiceCollection AddIndexerClients(this IServiceCollection services)
    {
        services
            .AddHttpClient<TorznabIndexerClient>(client =>
            {
                client.Timeout = IndexerRequestTimeout;
                client.MaxResponseContentBufferSize = MaxResponseBytes;
            })
            .ConfigurePrimaryHttpMessageHandler(CreateSsrfSafeHandler);

        services
            .AddHttpClient<DefinitionIndexerClient>(client =>
            {
                client.Timeout = IndexerRequestTimeout;
                client.MaxResponseContentBufferSize = MaxResponseBytes;
            })
            .ConfigurePrimaryHttpMessageHandler(CreateSsrfSafeHandler);

        // The login sequence runs on its own client over the same hardened transport — it is an
        // indexer request like a search, only aimed at the login endpoint.
        services
            .AddHttpClient(IndexerSessionManager.HttpClientName, client =>
            {
                client.Timeout = IndexerRequestTimeout;
                client.MaxResponseContentBufferSize = MaxResponseBytes;
            })
            .ConfigurePrimaryHttpMessageHandler(CreateSsrfSafeHandler);

        // Singleton, not scoped: the per-indexer sign-in gate lives in it, so two concurrent
        // federated searches share one sign-in instead of racing two. It persists through its own
        // scopes precisely because it is one.
        services.AddSingleton<IIndexerSessionManager>(sp => new IndexerSessionManager(
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<IndexerCredentialProtector>(),
            sp.GetRequiredService<ILogger<IndexerSessionManager>>()));

        // The administration surface reaches the same instance through the narrow half of its
        // interface: it needs to forget a session when a credential changes, and nothing more.
        services.AddSingleton<IIndexerSessionInvalidator>(
            sp => sp.GetRequiredService<IIndexerSessionManager>());

        services
            .AddHttpClient<FlareSolverrClient>(client =>
            {
                // Not the indexer timeout: a browser solve is allowed longer than a plain request.
                client.Timeout = FlareSolverrClient.RequestTimeout;
                client.MaxResponseContentBufferSize = MaxResponseBytes;
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                ConnectTimeout = IndexerConnectTimeout,
                UseProxy = false,
                AllowAutoRedirect = false,
            });
        services.AddScoped<IIndexerBrowserTransport>(sp => sp.GetRequiredService<FlareSolverrClient>());

        // A login-walled site behind a browser challenge: requests go out as the browser that solved
        // it, through the same egress proxy, because the clearance is bound to that address. The proxy
        // is the fail-closed guard FlareSolverr already uses — it refuses private destinations itself.
        services
            .AddHttpClient(IndexerClearanceCache.HttpClientName, client =>
            {
                client.Timeout = IndexerRequestTimeout;
                client.MaxResponseContentBufferSize = MaxResponseBytes;
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                ConnectTimeout = IndexerConnectTimeout,
                Proxy = new System.Net.WebProxy(FlareSolverrClient.ProxyUrl),
                UseProxy = true,
                // Cookies are sent explicitly from the session jar and the clearance, never from a
                // container that would rotate invisibly with the handler pool.
                UseCookies = false,
                AllowAutoRedirect = false,
            });

        // Singleton for its per-indexer solve gate and the clearances it keeps. The solver is built per
        // solve from the factory, so no scoped or transient client is captured for the process's life.
        services.AddSingleton<IIndexerClearance>(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            return new IndexerClearanceCache(
                () => new FlareSolverrClient(factory.CreateClient(nameof(FlareSolverrClient))),
                TimeProvider.System);
        });

        services.AddScoped<IIndexerClient, IndexerClientFactory>();

        // Registered with the real adapters it runs on: a host that substitutes its own indexer client
        // has no sessions to fetch a member-only file with, and Downloads then fetches every link as before.
        services.AddScoped<IReleaseFileSource, ReleaseFileSource>();

        return services;
    }

    // The handler itself lives in the Kernel: it is the same transport Metadata, Subtitles,
    // Notifications and Downloads use, and one security surface beats five copies of it.
    private static SocketsHttpHandler CreateSsrfSafeHandler()
    {
        var handler = SsrfSafeHttpHandler.Create(IndexerConnectTimeout, CinomniTelemetry.Modules.Discovery);
        // The definition client's session sends its cookies explicitly, from the per-indexer jar the
        // session manager keeps; the handler's own container (on by default) would capture Set-Cookie
        // into a jar that rotates invisibly with the handler pool and fight the explicit one. Nothing
        // else in this module uses cookies — Torznab/Newznab authenticate with the apikey query
        // parameter — so turning the container off leaves every other adapter byte-for-byte unchanged.
        handler.UseCookies = false;
        return handler;
    }

    /// <summary>
    /// Applies pending migrations for the discovery schema and makes sure the monthly partitions the
    /// next searches will be written into exist (both idempotent). Ensuring partitions at startup
    /// rather than only on the daily job matters after a long shutdown: the process would otherwise
    /// serve searches into the DEFAULT partition until the first retention tick.
    /// <para>
    /// The migration is fatal — an unmigrated schema is not a schema this module can serve. Partition
    /// maintenance is <b>not</b>: it is housekeeping, the DEFAULT partition guarantees every insert
    /// still succeeds without it, and no housekeeping step may be able to keep the whole installation
    /// from booting. A failure is logged loudly and startup continues; the daily
    /// <c>discovery.retention</c> job retries it and reports the same condition.
    /// </para>
    /// </summary>
    public static async Task MigrateDiscoveryAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);

        var loggerFactory = scope.ServiceProvider.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance;
        var logger = loggerFactory.CreateLogger(typeof(DiscoveryModule));

        try
        {
            var maintenance = await SearchPartitions.EnsureAsync(
                dbContext, DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime), cancellationToken);

            if (maintenance.Blocked.Count > 0)
            {
                logger.LogWarning(
                    "Search history could not attach {Blocked} monthly partitions ({Names}) because rows for "
                    + "those months already sit in the DEFAULT partition. Searches continue to be recorded "
                    + "there; move those rows into their month to restore O(1) retention drops.",
                    maintenance.Blocked.Count,
                    string.Join(", ", maintenance.Blocked));
            }
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            logger.LogError(
                failure,
                "Search-history partition maintenance failed at startup. Discovery keeps working — new rows "
                + "fall into the DEFAULT partition — and the discovery.retention job retries this.");
        }
    }

}
