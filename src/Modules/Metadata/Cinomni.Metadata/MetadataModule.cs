using Cinomni.Kernel.Diagnostics;
using Cinomni.Kernel.Messaging;
using Cinomni.Kernel.Net;
using Cinomni.Metadata.Application;
using Cinomni.Metadata.Contracts;
using Cinomni.Metadata.Messaging;
using Cinomni.Metadata.Persistence;
using Cinomni.Metadata.Providers;
using Cinomni.Operations;
using Cinomni.Operations.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Metadata;

/// <summary>
/// Registers the Metadata module: the anti-corruption layer to external metadata providers. It
/// fetches a neutral <c>MetadataSnapshot</c> for a work — selecting its artwork — and emits
/// <c>MetadataRefreshed</c>; Catalog consumes that and copies the fields onto its own work (identity
/// stays internal). Metadata is a pure supplier — it never references Catalog. Requires the
/// platform kernel. Multi-provider: the domain consumes every registered <see cref="IMetadataSource"/>
/// (filtered by media kind, ordered by priority); the production adapters are registered separately
/// (<see cref="AddMetadataAdapters"/>) so tests can substitute fakes.
/// </summary>
public static class MetadataModule
{
    private static readonly TimeSpan FetchConnectTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Cap on a metadata-provider response we will buffer (a hostile provider must not exhaust memory).</summary>
    private const long MaxResponseBytes = 16 * 1024 * 1024;

    public static IServiceCollection AddMetadataModule(this IServiceCollection services)
    {
        services.AddModuleDbContext<MetadataDbContext>();
        // Which region's age classification this installation reads. Live, and with no default: until
        // an administrator names one, every provider reports no classification rather than this
        // guessing a country from whichever the provider happened to list first.
        ContentRatingOptions BindContentRating(SettingsView view) => new()
        {
            Region = view.GetString(ContentRatingSettingDefinitions.Region)?.Trim().ToUpperInvariant()
                ?? string.Empty,
        };

        services.AddSettingDefinition(ContentRatingSettingDefinitions.Region);
        services.AddLiveOptions(BindContentRating);
        // Last registration wins. Identity and Catalog register an unset fallback so a composition
        // without this module still resolves; this one is what a running installation actually reads.
        services.AddSingleton<IContentRatingRegion, LiveContentRatingRegion>();

        // The provider credentials, settable from the console as encrypted secrets and read live by the
        // adapters. The configuration path of each is its fallback tier, so an environment variable still
        // wins and an installation configured from a file behaves exactly as before. Registered here
        // rather than beside the adapters, like the region: a composition of the adapters alone has no
        // settings store, and they then read the keys they were composed with.
        foreach (var definition in MetadataProviderKeyDefinitions.All)
        {
            services.AddSettingDefinition(definition);
        }

        services.AddLiveOptions(MetadataProviderKeys.Bind);

        services.AddIntegrationEvent<MetadataRefreshed>(MetadataEventNames.MetadataRefreshed);
        services.AddIntegrationEvent<MetadataRefreshFailed>(MetadataEventNames.MetadataRefreshFailed);
        services.AddIntegrationEvent<MetadataArtworkSelected>(MetadataEventNames.MetadataArtworkSelected);
        services.AddIntegrationEvent<ProviderDegraded>(MetadataEventNames.ProviderDegraded);

        services.AddCommand<RefreshMetadataCommand>(MetadataCommandNames.RefreshMetadata);
        services.AddCommand<RefreshContinuingSeriesCommand>(MetadataCommandNames.RefreshContinuingSeries);
        services.AddCommand<PurgeSnapshotsCommand>(MetadataCommandNames.PurgeSnapshots);

        services.AddScoped<IMetadataSearch, MetadataSearchService>();
        services.AddScoped<IMetadataQuery, MetadataQuery>();
        services.AddScoped<IMetadataRefresh, MetadataRefreshService>();
        services.AddScoped<IMetadataArtwork, ArtworkSelectionService>();

        services.AddScoped<ICommandHandler<RefreshMetadataCommand>, RefreshMetadataCommandHandler>();
        services.AddScoped<ICommandHandler<RefreshContinuingSeriesCommand>, RefreshContinuingSeriesCommandHandler>();
        services.AddScoped<ICommandHandler<PurgeSnapshotsCommand>, PurgeSnapshotsCommandHandler>();

        return services;
    }

    /// <summary>
    /// Registers the periodic sweep that keeps a still-airing series current. It lives beside
    /// <see cref="AddMetadataAdapters"/> rather than inside <see cref="AddMetadataModule"/> because it
    /// needs the refresh profile, and because a test host must be able to register the module without
    /// acquiring a background cadence it never drives.
    /// </summary>
    private static void AddContinuingSeriesJob(IServiceCollection services, MetadataOptions profile) =>
        services.AddScheduledJob<RefreshContinuingSeriesCommand>(
            "metadata.refresh-continuing-series",
            MetadataCommandNames.RefreshContinuingSeries,
            profile.ContinuingSeriesSweepInterval);

    /// <summary>
    /// Registers the sweep that prunes superseded snapshots. It sits beside the refresh sweep for the
    /// same reason: it is a deployment cadence read once at registration, and a test host must be able
    /// to register the module without acquiring it.
    /// </summary>
    private static void AddSnapshotRetentionJob(IServiceCollection services, MetadataOptions profile) =>
        services.AddScheduledJob<PurgeSnapshotsCommand>(
            "metadata.retention",
            MetadataCommandNames.PurgeSnapshots,
            profile.SnapshotPurgeInterval);

    /// <summary>
    /// Registers the production providers (TMDB, TheTVDB, TVMaze) over SSRF-hardened clients plus the
    /// refresh profile and per-provider options. Each concrete adapter is a distinct typed client
    /// (avoiding the shared-name collision of registering the same interface repeatedly) forwarded to
    /// <see cref="IMetadataSource"/>, so the domain consumes them all. Also registers the
    /// continuing-series sweep, which is a deployment cadence rather than a domain rule. Tests skip this
    /// and register their own fakes plus a <see cref="MetadataOptions"/>.
    /// </summary>
    public static IServiceCollection AddMetadataAdapters(
        this IServiceCollection services,
        Action<MetadataOptions>? configureProfile = null,
        Action<TmdbProviderOptions>? configureTmdb = null,
        Action<TvdbProviderOptions>? configureTvdb = null,
        Action<TvMazeProviderOptions>? configureTvMaze = null)
    {
        var profile = new MetadataOptions();
        configureProfile?.Invoke(profile);
        // Retention values become a scheduled cadence and a deletion cutoff, so they are checked here
        // — where an operator sees the failure at startup — rather than inside the sweep.
        profile.Validate();
        services.AddSingleton(profile);
        AddContinuingSeriesJob(services, profile);
        AddSnapshotRetentionJob(services, profile);

        // The settings store's catalogue for MetadataOptions: only SnapshotRetention and
        // SnapshotPurgeInterval carry a settings key today. The binder closes over the already-validated
        // `profile` for everything else — providers, TTLs, backoff, language — none of which is
        // browser-editable in this increment. The purge cadence (SnapshotPurgeInterval) is captured once
        // by AddSnapshotRetentionJob above and only picks up a stored change after a restart.
        MetadataOptions BindProfile(SettingsView view) => new()
        {
            Providers = profile.Providers,
            RefreshTtl = profile.RefreshTtl,
            SeriesRefreshTtl = profile.SeriesRefreshTtl,
            ContinuingSeriesSweepInterval = profile.ContinuingSeriesSweepInterval,
            MaxContinuingRefreshesPerSweep = profile.MaxContinuingRefreshesPerSweep,
            BackoffBase = profile.BackoffBase,
            BackoffCap = profile.BackoffCap,
            DegradeAfterAttempts = profile.DegradeAfterAttempts,
            Language = profile.Language,
            SnapshotRetention =
                view.GetTimeSpan(MetadataSettingDefinitions.SnapshotRetention) ?? profile.SnapshotRetention,
            SnapshotPurgeInterval =
                view.GetTimeSpan(MetadataSettingDefinitions.SnapshotPurgeInterval) ?? profile.SnapshotPurgeInterval,
        };

        services.AddSettingsCheck(MetadataSettingDefinitions.All, BindProfile, MetadataOptions.Check);
        services.AddLiveOptions(BindProfile);


        // One per process, shared by every adapter: the latch that keeps "this provider has no key" a
        // single line at first use instead of one line per search or per work in a refresh sweep.
        services.AddSingleton<DisabledProviderNotice>();

        var tmdbOptions = Configure(new TmdbProviderOptions(), configureTmdb);
        var tvdbOptions = Configure(new TvdbProviderOptions(), configureTvdb);
        var tvMazeOptions = Configure(new TvMazeProviderOptions(), configureTvMaze);

        services.AddSingleton(tmdbOptions);
        services.AddSingleton(tvdbOptions);
        services.AddSingleton(tvMazeOptions);

        AddProvider<TmdbMetadataSource>(services, tmdbOptions);
        // Replaces Catalog's empty fallback. Last registration wins, and this one can actually call TMDB.
        services.AddScoped<IMetadataLists, TmdbTrendingLists>();
        AddProvider<TvMazeMetadataSource>(services, tvMazeOptions);

        // TheTVDB authenticates with a bearer JWT; a singleton owns the token cache and logs in over its
        // own SSRF-hardened client, while the data adapter is the usual typed client.
        services.AddSingleton<TvdbTokenProvider>();
        services
            .AddHttpClient(TvdbTokenProvider.AuthClientName, client => ConfigureClient(client, tvdbOptions))
            .ConfigurePrimaryHttpMessageHandler(CreateSsrfSafeHandler);
        AddProvider<TvdbMetadataSource>(services, tvdbOptions);

        return services;
    }

    /// <summary>Applies pending migrations for the metadata schema (idempotent).</summary>
    public static async Task MigrateMetadataAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MetadataDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);
    }

    private static TOptions Configure<TOptions>(TOptions options, Action<TOptions>? configure)
        where TOptions : MetadataProviderOptions
    {
        configure?.Invoke(options);
        return options;
    }

    // Register a concrete provider as its own typed client (distinct HttpClient name = no config
    // collision), then forward it to IMetadataSource so the domain enumerates every provider.
    private static void AddProvider<TSource>(IServiceCollection services, MetadataProviderOptions options)
        where TSource : class, IMetadataSource
    {
        services
            .AddHttpClient<TSource>(client => ConfigureClient(client, options))
            .ConfigurePrimaryHttpMessageHandler(CreateSsrfSafeHandler);
        services.AddTransient<IMetadataSource>(sp => sp.GetRequiredService<TSource>());
    }

    private static void ConfigureClient(HttpClient client, MetadataProviderOptions options)
    {
        client.BaseAddress = new Uri(options.BaseAddress);
        client.Timeout = options.Timeout;
        client.MaxResponseContentBufferSize = MaxResponseBytes;
        client.DefaultRequestHeaders.Add("User-Agent", options.UserAgent);
    }

    // The handler itself lives in the Kernel: it is the same transport Discovery, Subtitles,
    // Notifications and Downloads use, and one security surface beats five copies of it.
    private static SocketsHttpHandler CreateSsrfSafeHandler() =>
        SsrfSafeHttpHandler.Create(FetchConnectTimeout, CinomniTelemetry.Modules.Metadata);
}
