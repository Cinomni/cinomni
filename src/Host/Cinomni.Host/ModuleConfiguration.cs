using Cinomni.Downloads;
using Cinomni.Downloads.Application;
using Cinomni.Downloads.Contracts;
using Cinomni.Downloads.Engine;
using Cinomni.Import;
using Cinomni.Import.Application;
using Cinomni.Import.Probe;
using Cinomni.Metadata;
using Cinomni.Metadata.Application;
using Cinomni.Metadata.Providers;
using Cinomni.Playback;
using Cinomni.Playback.Application;
using Cinomni.Playback.Encoding;
using Cinomni.Subtitles;
using Cinomni.Subtitles.Application;
using Cinomni.Subtitles.Providers;

namespace Cinomni.Host;

/// <summary>
/// Binds the <c>Metadata</c>, <c>Import</c>, <c>Downloads</c>, <c>Playback</c> and <c>Subtitles</c>
/// configuration sections onto the adapter options the composition root registers. Every adapter
/// registration takes <c>Action&lt;TOptions&gt;</c> callbacks rather than an <c>IOptions</c> binding,
/// because the values are read once at registration time — the continuing-series sweep interval, for
/// instance, becomes a scheduled-job cadence and cannot change afterwards.
/// <para>
/// Every value is read explicitly rather than through a blanket <c>Bind</c>: a typo in a section then
/// leaves the default in place instead of silently reshaping a provider adapter, and the properties
/// that are genuinely configuration are visible here in one list.
/// </para>
/// </summary>
internal static class ModuleConfiguration
{
    /// <summary>
    /// Registers the production metadata providers with the configured refresh profile, per-provider
    /// keys and — the correctness-critical one — TheTVDB season ordering, which decides which SxxEyy
    /// numbers reach the catalog and therefore which release matches which episode.
    /// </summary>
    /// <param name="retention">
    /// Applies the <c>Retention:Metadata</c> window onto the same profile, because Metadata keeps its
    /// snapshot retention beside its refresh cadence rather than in a second options type.
    /// </param>
    public static IServiceCollection AddConfiguredMetadataAdapters(
        this IServiceCollection services,
        IConfiguration configuration,
        RetentionConfiguration retention)
    {
        var section = configuration.GetSection("Metadata");

        return services.AddMetadataAdapters(
            configureProfile: options =>
            {
                options.Providers = section.GetSection("Providers").Get<string[]>() ?? options.Providers;
                options.Language = Text(section, "Language") ?? options.Language;
                options.RefreshTtl = Duration(section, "RefreshTtl", options.RefreshTtl);
                options.SeriesRefreshTtl = Duration(section, "SeriesRefreshTtl", options.SeriesRefreshTtl);
                options.ContinuingSeriesSweepInterval = Duration(
                    section, "ContinuingSeriesSweepInterval", options.ContinuingSeriesSweepInterval);
                options.MaxContinuingRefreshesPerSweep = Number(
                    section, "MaxContinuingRefreshesPerSweep", options.MaxContinuingRefreshesPerSweep);
                retention.Metadata(options);
            },
            configureTmdb: options =>
            {
                var provider = section.GetSection("Tmdb");
                options.ApiKey = Text(provider, "ApiKey") ?? string.Empty;
                options.Language = Text(provider, "Language") ?? Text(section, "Language") ?? options.Language;
                options.MaxSeasonRequests = Number(provider, "MaxSeasonRequests", options.MaxSeasonRequests);
            },
            configureTvdb: options =>
            {
                var provider = section.GetSection("Tvdb");
                options.ApiKey = Text(provider, "ApiKey") ?? string.Empty;
                options.Pin = Text(provider, "Pin");
                options.Language = Text(provider, "Language") ?? Text(section, "Language") ?? options.Language;
                options.SeasonType = Text(provider, "SeasonType") ?? options.SeasonType;
                options.MaxEpisodePages = Number(provider, "MaxEpisodePages", options.MaxEpisodePages);
            },
            configureTvMaze: options =>
            {
                var provider = section.GetSection("TvMaze");
                options.Language = Text(provider, "Language") ?? Text(section, "Language") ?? options.Language;
            });
    }

    /// <summary>
    /// Registers the production filesystem and ffprobe adapters with the configured library root, the
    /// per-work-kind size floors and the naming layout. <see cref="ImportOptions.MinEpisodeBytes"/> in
    /// particular must be reachable: the movie floor it replaces silently discards every file of a
    /// legitimate season pack of short episodes.
    /// </summary>
    public static IServiceCollection AddConfiguredImportAdapters(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var section = configuration.GetSection("Import");

        return services.AddImportAdapters(
            configureImport: options =>
            {
                options.LibraryRoot = Text(section, "LibraryRoot") ?? options.LibraryRoot;

                // The only directory an import may read from. It defaults to the directory the sidecar
                // downloads into, because that is what it is: the two processes must already see that
                // path identically, and asking an operator to configure the same thing twice is how the
                // two drift apart and confinement quietly starts rejecting every job.
                options.StagingRoot = Text(section, "StagingRoot")
                    ?? Text(configuration.GetSection("Downloads").GetSection("Sidecar"), "StagingPath")
                    ?? options.StagingRoot;
                options.MinVideoBytes = Size(section, "MinVideoBytes", options.MinVideoBytes);
                options.MinEpisodeBytes = Size(section, "MinEpisodeBytes", options.MinEpisodeBytes);
                options.SeasonFolderPrefix = Text(section, "SeasonFolderPrefix") ?? options.SeasonFolderPrefix;
                options.SeriesFolderIncludesYear = section.GetValue(
                    "SeriesFolderIncludesYear", options.SeriesFolderIncludesYear);
                options.MaxSegmentLength = Number(section, "MaxSegmentLength", options.MaxSegmentLength);
                options.MaxTargetPathLength = Number(section, "MaxTargetPathLength", options.MaxTargetPathLength);

                var extensions = section.GetSection("VideoExtensions").Get<string[]>();
                if (extensions is { Length: > 0 })
                {
                    options.VideoExtensions = new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);
                }
            },
            configureFfprobe: options =>
            {
                var ffprobe = section.GetSection("Ffprobe");
                options.BinaryPath = Text(ffprobe, "BinaryPath") ?? options.BinaryPath;
                options.Timeout = Duration(ffprobe, "Timeout", options.Timeout);
            });
    }

    /// <summary>
    /// Registers the production libtorrent-sidecar engine with the configured gRPC endpoint and staging
    /// directory. <see cref="SidecarOptions.StagingPath"/> is the value a packaged deployment cannot leave
    /// to a code default: it is handed to the sidecar as the download save path and later reopened by
    /// Import, so the two processes must see that directory at the same absolute path.
    /// </summary>
    public static IServiceCollection AddConfiguredDownloadsAdapters(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var downloads = configuration.GetSection("Downloads");
        var sidecar = downloads.GetSection("Sidecar");
        var engine = Text(downloads, "Engine");
        if (string.Equals(engine, "Qbittorrent", StringComparison.OrdinalIgnoreCase))
        {
            // The staging directory is not the sidecar's alone: it is the save path handed to whichever
            // engine downloads, and the root Import, backup and the startup checks all read. Without it
            // the host could not start at all with the qBittorrent engine. Only the path is taken; there
            // is no sidecar to address or authenticate to.
            var staging = new SidecarOptions();
            staging.StagingPath = Text(sidecar, "StagingPath") ?? staging.StagingPath;
            services.AddSingleton(staging);

            var qbittorrent = downloads.GetSection("Qbittorrent");
            return services.AddQbittorrentEngine(options =>
            {
                options.BaseAddress = Text(qbittorrent, "BaseAddress") ?? string.Empty;
                options.Username = Text(qbittorrent, "Username") ?? options.Username;
                options.Password = Text(qbittorrent, "Password") ?? string.Empty;
                options.Timeout = Duration(qbittorrent, "Timeout", options.Timeout);
            });
        }

        return services.AddSidecarTorrentEngine(options =>
        {
            options.Address = Text(sidecar, "Address") ?? options.Address;
            options.StagingPath = Text(sidecar, "StagingPath") ?? options.StagingPath;
            // The credential for the control port. Its absence is not an error — the base topology
            // keeps the port on a private network with nothing published — and it is read here by
            // name only: the value never reaches a log record, an error or telemetry.
            options.ControlToken = Text(sidecar, "ControlToken") ?? options.ControlToken;
            options.CallTimeout = Duration(sidecar, "CallTimeout", options.CallTimeout);
        });
    }

    /// <summary>
    /// Reads the opt-in tunnel guard from <c>Downloads:Tunnel</c>.
    /// <para>
    /// Nothing here has an opinion unless <c>Device</c> names an interface. That is what makes the
    /// tunnel opt-in rather than a precondition: an installation with no tunnel — every development
    /// checkout, and the default packaged topology — reads this section, finds no device, and behaves
    /// exactly as it did before the guard existed.
    /// </para>
    /// <para>
    /// The policy is the one setting where an unreadable value must not fall back to "leave it as it
    /// was": a misspelt mode becomes <see cref="TunnelLossPolicy.Block"/>, because the alternative is
    /// a typo that silently disables a kill-switch.
    /// </para>
    /// </summary>
    public static void ApplyTunnelGuard(IConfiguration configuration, TunnelOptions options)
    {
        var section = configuration.GetSection("Downloads").GetSection("Tunnel");

        options.Device = Text(section, "Device")?.Trim() ?? options.Device;
        options.LossPolicy = ParsePolicy(Text(section, "LossPolicy"), options.LossPolicy);
        options.PollInterval = Duration(section, "PollInterval", options.PollInterval);
        options.UnverifiedThreshold = Number(section, "UnverifiedThreshold", options.UnverifiedThreshold);
        options.VerifiedThreshold = Number(section, "VerifiedThreshold", options.VerifiedThreshold);
    }

    /// <summary>
    /// Reads <c>Downloads:Transfers</c>: the stall timeout and the seeding rule new downloads get. A
    /// seeding bound set to an empty value or <c>none</c> removes that bound; left out, it keeps the
    /// default.
    /// </summary>
    public static void ApplyTransfers(IConfiguration configuration, TransferOptions options)
    {
        var section = configuration.GetSection("Downloads").GetSection("Transfers");

        options.StallTimeout = Duration(section, "StallTimeout", options.StallTimeout);
        options.SeedRatioLimit = IsUnbounded(section, "SeedRatioLimit")
            ? null
            : section.GetValue<double?>("SeedRatioLimit") ?? options.SeedRatioLimit;
        options.SeedTimeLimit = IsUnbounded(section, "SeedTimeLimit")
            ? null
            : section.GetValue<TimeSpan?>("SeedTimeLimit") ?? options.SeedTimeLimit;
    }

    private static bool IsUnbounded(IConfiguration section, string key) =>
        section[key] is { } value
        && (value.Trim().Length == 0 || value.Trim().Equals("none", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A configured loss policy, falling back to the strongest mode rather than to a typo.
    /// <para>
    /// Hyphens and underscores are removed before parsing so that the sidecar's spelling
    /// (<c>pause-and-alert</c>) and this enum's (<c>PauseAndAlert</c>) are the same setting. The two
    /// processes enforce one policy, so they must not need two variables to be told it.
    /// </para>
    /// </summary>
    private static TunnelLossPolicy ParsePolicy(string? value, TunnelLossPolicy fallback)
    {
        if (value is null)
        {
            return fallback;
        }

        var normalized = value.Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal);

        return Enum.TryParse<TunnelLossPolicy>(normalized, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : TunnelLossPolicy.Block;
    }

    /// <summary>
    /// Registers the production FFmpeg encoder with the configured transcode root, binary path, HLS
    /// segment length, transcode limits, idle timeout and maximum lifetime. The codec whitelists stay in code on purpose — they are a security boundary
    /// (SECURITY.md, external processes), not an operator setting.
    /// </summary>
    /// <param name="retention">
    /// Applies the <c>Retention:Playback</c> window onto the same options, because Playback keeps its
    /// session retention beside its transcode settings rather than in a second options type.
    /// </param>
    public static IServiceCollection AddConfiguredPlaybackAdapters(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<PlaybackOptions> retention)
    {
        var section = configuration.GetSection("Playback");
        var ffmpegBinaryPath = Text(section.GetSection("Ffmpeg"), "BinaryPath");

        return services.AddPlaybackAdapters(
            configurePlayback: options =>
            {
                options.TranscodeRoot = Text(section, "TranscodeRoot") ?? options.TranscodeRoot;
                options.MaxConcurrentTranscodes = Number(section, "MaxConcurrentTranscodes", options.MaxConcurrentTranscodes);
                options.MaxTranscodesPerAccount = Number(section, "MaxTranscodesPerAccount", options.MaxTranscodesPerAccount);
                options.TranscodeIdleTimeout = Duration(section, "TranscodeIdleTimeout", options.TranscodeIdleTimeout);
                options.MaxTranscodeLifetime = Duration(section, "MaxTranscodeLifetime", options.MaxTranscodeLifetime);
                retention(options);
            },
            configureFfmpeg: options =>
            {
                var ffmpeg = section.GetSection("Ffmpeg");
                options.BinaryPath = Text(ffmpeg, "BinaryPath") ?? options.BinaryPath;
                // The same ffprobe Import runs, unless Playback names its own.
                options.ProbeBinaryPath = Text(ffmpeg, "ProbeBinaryPath")
                    ?? Text(configuration.GetSection("Import:Ffprobe"), "BinaryPath")
                    ?? options.ProbeBinaryPath;
                options.SegmentSeconds = Number(ffmpeg, "SegmentSeconds", options.SegmentSeconds);
            },
            configureHardwareProbe: options =>
            {
                // The same ffmpeg binary the encoder itself launches — probing a different one would
                // answer for hardware the encoder never actually gets to use.
                options.BinaryPath = ffmpegBinaryPath ?? options.BinaryPath;
                options.DevicePath = Text(section.GetSection("HardwareAcceleration"), "DevicePath") ?? options.DevicePath;
                options.NvidiaDevicePath =
                    Text(section.GetSection("HardwareAcceleration"), "NvidiaDevicePath") ?? options.NvidiaDevicePath;
            });
    }

    /// <summary>
    /// Registers the production subtitle provider and file store with the configured profile. The
    /// provider key comes from configuration (environment or secret manager) and is never committed; an
    /// empty key leaves the provider registered but unusable, which is how the module degrades.
    /// </summary>
    public static IServiceCollection AddConfiguredSubtitleAdapters(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var section = configuration.GetSection("Subtitles");

        return services.AddSubtitleAdapters(
            configureProfile: options =>
            {
                // An empty array would silently switch subtitles off; only a non-empty list overrides.
                var languages = section.GetSection("WantedLanguages").Get<string[]>();
                if (languages is { Length: > 0 })
                {
                    options.WantedLanguages = languages;
                }

                options.MinScore = Number(section, "MinScore", options.MinScore);
                options.HearingImpaired = section.GetValue("HearingImpaired", options.HearingImpaired);
                options.Forced = section.GetValue("Forced", options.Forced);
                options.ProviderCallInterval = Duration(
                    section, "ProviderCallInterval", options.ProviderCallInterval);
            },
            configureProvider: options =>
            {
                var provider = section.GetSection("Provider");
                options.ApiKey = Text(provider, "ApiKey") ?? string.Empty;
                options.BaseAddress = Text(provider, "BaseAddress") ?? options.BaseAddress;
                options.UserAgent = Text(provider, "UserAgent") ?? options.UserAgent;
                options.Timeout = Duration(provider, "Timeout", options.Timeout);
            },
            configureSubdl: options =>
            {
                var subdl = section.GetSection("Subdl");
                options.ApiKey = Text(subdl, "ApiKey") ?? string.Empty;
                options.BaseAddress = Text(subdl, "BaseAddress") ?? options.BaseAddress;
                options.UserAgent = Text(subdl, "UserAgent") ?? options.UserAgent;
                options.Timeout = Duration(subdl, "Timeout", options.Timeout);
            });
    }

    /// <summary>A configured string, or null when the key is absent or blank (a blank key is "not set").</summary>
    private static string? Text(IConfiguration section, string key)
    {
        var value = section[key];
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static TimeSpan Duration(IConfiguration section, string key, TimeSpan fallback) =>
        section.GetValue<TimeSpan?>(key) ?? fallback;

    private static int Number(IConfiguration section, string key, int fallback) =>
        section.GetValue<int?>(key) ?? fallback;

    private static long Size(IConfiguration section, string key, long fallback) =>
        section.GetValue<long?>(key) ?? fallback;
}
