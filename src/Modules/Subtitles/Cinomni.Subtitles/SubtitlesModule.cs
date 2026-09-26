using Cinomni.Import.Contracts;
using Cinomni.Kernel.Diagnostics;
using Cinomni.Kernel.Messaging;
using Cinomni.Kernel.Net;
using Cinomni.Library.Contracts;
using Cinomni.Operations;
using Cinomni.Operations.Settings;
using Cinomni.Subtitles.Application;
using Cinomni.Subtitles.Contracts;
using Cinomni.Subtitles.EventHandlers;
using Cinomni.Subtitles.Files;
using Cinomni.Subtitles.Messaging;
using Cinomni.Subtitles.Persistence;
using Cinomni.Subtitles.Providers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Subtitles;

/// <summary>
/// Registers the Subtitles module: reacts to Library's <c>MediaAssetRegistered</c> by searching
/// external providers for the asset's missing subtitle languages, downloading the best candidate,
/// writing it next to the video and emitting <c>SubtitleAvailable</c> (Library enriches its asset).
/// Reads the asset by interface from Library and its catalog units by interface from Catalog (so an
/// episode search carries <c>SxxEyy</c>); never mutates either. Requires the platform kernel. The
/// provider and file-store adapters are registered separately (<see cref="AddSubtitleAdapters"/>) so
/// tests can substitute fakes.
/// </summary>
public static class SubtitlesModule
{
    private static readonly TimeSpan FetchConnectTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Cap on a subtitle-provider response we will buffer (a hostile provider must not exhaust memory).</summary>
    private const long MaxResponseBytes = 16 * 1024 * 1024;

    public static IServiceCollection AddSubtitlesModule(this IServiceCollection services)
    {
        services.AddModuleDbContext<SubtitlesDbContext>();

        SubtitlePreference BindPreference(SettingsView view)
        {
            var languages = view.GetStringArray(SubtitlePreferenceDefinitions.WantedLanguages);
            return new SubtitlePreference
            {
                WantedLanguages = languages is null
                    ? ["en"]
                    : languages
                        .Select(language => language.Trim().ToLowerInvariant())
                        .Where(language => language.Length == 2)
                        .Distinct(StringComparer.Ordinal)
                        .Take(SubtitlePreferenceDefinitions.MaxLanguages)
                        .ToList(),
                HearingImpaired = view.GetBool(SubtitlePreferenceDefinitions.HearingImpaired) ?? false,
                Forced = view.GetBool(SubtitlePreferenceDefinitions.Forced) ?? false,
            };
        }

        services.AddSettingsCheck(SubtitlePreferenceDefinitions.All, BindPreference, SubtitlePreference.Check);
        services.AddLiveOptions(BindPreference);

        services.AddIntegrationEvent<SubtitleSearchRequested>(SubtitleEventNames.SubtitleSearchRequested);
        services.AddIntegrationEvent<SubtitleAvailable>(SubtitleEventNames.SubtitleAvailable);
        services.AddIntegrationEvent<SubtitleSearchFailed>(SubtitleEventNames.SubtitleSearchFailed);

        services.AddCommand<SearchSubtitlesCommand>(SubtitleCommandNames.SearchSubtitles);
        services.AddCommand<RelocateSubtitlesCommand>(SubtitleCommandNames.RelocateSubtitles);
        services.AddCommand<CatchUpSubtitlesCommand>(SubtitleCommandNames.CatchUp);

        services.AddScoped<ISubtitleSearch, SubtitleSearchService>();
        services.AddScoped<ISubtitleQuery, SubtitleQuery>();
        services.AddScoped<EpisodeContextResolver>();

        // Singleton on purpose: the burst it flattens spans scopes — a season pack registers N assets
        // in one import and every one of them enqueues its own search-subtitles command.
        services.AddSingleton<SubtitleProviderThrottle>();

        // The viewer-scoped twin the endpoints bind; it consults Catalog's content access (→i).
        services.AddScoped<SubtitleBrowse>();

        services.AddScoped<ICommandHandler<SearchSubtitlesCommand>, SearchSubtitlesCommandHandler>();
        services.AddScoped<ICommandHandler<RelocateSubtitlesCommand>, RelocateSubtitlesCommandHandler>();
        services.AddScoped<ICommandHandler<CatchUpSubtitlesCommand>, CatchUpSubtitlesCommandHandler>();
        services.AddScoped<SubtitleCatchUp>();
        services.AddScheduledJob<CatchUpSubtitlesCommand>(
            "subtitles.catch-up", SubtitleCommandNames.CatchUp, TimeSpan.FromHours(6));

        // Consumes Library's MediaAssetRegistered → enqueue a SearchSubtitles command.
        services.AddScoped<IEventHandler<MediaAssetRegistered>, MediaAssetRegisteredHandler>();

        // Consumes Import's MediaFileRelocated → enqueue a RelocateSubtitles command.
        services.AddScoped<IEventHandler<MediaFileRelocated>, MediaFileRelocatedHandler>();

        return services;
    }

    /// <summary>
    /// Registers the production OpenSubtitles provider (over an SSRF-hardened client) and the local
    /// file store, plus the subtitle profile options. Tests skip this and register their own
    /// <see cref="ISubtitleProvider"/>/<see cref="ISubtitleFileStore"/> fakes plus a
    /// <see cref="SubtitleOptions"/>.
    /// </summary>
    public static IServiceCollection AddSubtitleAdapters(
        this IServiceCollection services,
        Action<SubtitleOptions>? configureProfile = null,
        Action<SubtitleProviderOptions>? configureProvider = null,
        Action<SubdlProviderOptions>? configureSubdl = null)
    {
        var profile = new SubtitleOptions();
        configureProfile?.Invoke(profile);
        services.AddSingleton(profile);

        var providerOptions = new SubtitleProviderOptions();
        configureProvider?.Invoke(providerOptions);
        services.AddSingleton(providerOptions);

        services.AddSingleton<ISubtitleFileStore, LocalSubtitleFileStore>();

        services
            .AddHttpClient<ISubtitleProvider, OpenSubtitlesProvider>(client =>
            {
                client.BaseAddress = new Uri(providerOptions.BaseAddress);
                client.Timeout = providerOptions.Timeout;
                client.MaxResponseContentBufferSize = MaxResponseBytes;
                client.DefaultRequestHeaders.Add("User-Agent", providerOptions.UserAgent);
                // No Api-Key here: a default header rides on every request, and the file itself is
                // fetched from whatever host the download response names. The provider adds the key
                // to its own API calls only.
            })
            .ConfigurePrimaryHttpMessageHandler(CreateSsrfSafeHandler);

        var subdlOptions = new SubdlProviderOptions();
        configureSubdl?.Invoke(subdlOptions);
        services.AddSingleton(subdlOptions);
        services
            .AddHttpClient<SubdlSubtitleProvider>(client =>
            {
                client.BaseAddress = new Uri(subdlOptions.BaseAddress);
                client.Timeout = subdlOptions.Timeout;
                client.MaxResponseContentBufferSize = MaxResponseBytes;
                client.DefaultRequestHeaders.Add("User-Agent", subdlOptions.UserAgent);
            })
            .ConfigurePrimaryHttpMessageHandler(CreateSsrfSafeHandler);
        services.AddTransient<ISubtitleProvider>(sp => sp.GetRequiredService<SubdlSubtitleProvider>());

        return services;
    }

    /// <summary>Applies pending migrations for the subtitles schema (idempotent).</summary>
    public static async Task MigrateSubtitlesAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<SubtitlesDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);
    }

    // The handler itself lives in the Kernel: it is the same transport Discovery, Metadata,
    // Notifications and Downloads use, and one security surface beats five copies of it.
    private static SocketsHttpHandler CreateSsrfSafeHandler() =>
        SsrfSafeHttpHandler.Create(FetchConnectTimeout, CinomniTelemetry.Modules.Subtitles);
}
