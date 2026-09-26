using Cinomni.Import.Application;
using Cinomni.Host.Observability;
using Cinomni.Playback.Application;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Cinomni.Host.Health;

/// <summary>
/// Registers the readiness checks. Liveness registers nothing on purpose — see
/// <c>CinomniApi.MapCinomniEndpoints</c> for why the two probes must not share a dependency.
/// <para>
/// Each check resolves its subject from the options a module already bound, never from a second read of
/// configuration: a probe watching a different directory from the one Import actually writes to is
/// worse than no probe at all, and it is the kind of drift that only shows up when the disk is full.
/// </para>
/// </summary>
internal static class HealthRegistration
{
    /// <summary>Check names. Fixed words, never a path or an address — they reach an anonymous caller.</summary>
    public const string Postgres = "postgres";

    public const string Sidecar = "sidecar";

    public const string LibraryStorage = "library-storage";

    public const string TranscodeStorage = "transcode-storage";

    /// <param name="services">The composition root's service collection, with every module registered.</param>
    /// <param name="options">Readiness budget and free-space floor, already validated.</param>
    public static IServiceCollection AddCinomniHealthChecks(
        this IServiceCollection services,
        ObservabilityOptions options)
    {
        // The two storage roots are read on a cadence this process owns, never on the request path — a
        // free-space syscall on a stale network mount blocks for ever and no token can interrupt it.
        services.AddSingleton(sp => new StorageSpaceSampler(
            WatchedRoots(sp, options.MinFreeDiskBytes),
            DateTimeOffset.UtcNow));
        services.AddHostedService<StorageSpaceSamplerHostedService>();

        // The probes are anonymous, so the dependency work behind them is bounded rather than repeated
        // per request — see ReadinessSnapshot.
        services.AddSingleton(sp => new ReadinessSnapshot(
            sp.GetRequiredService<HealthCheckService>(),
            ReadinessSnapshot.DefaultMaxAge));

        services.AddHealthChecks()
            // The one hard dependency: every module's schema is on this connection.
            .Add(new HealthCheckRegistration(
                Postgres,
                sp => new PostgresHealthCheck(sp.GetRequiredService<IServiceScopeFactory>(), options.ReadinessTimeout),
                HealthStatus.Unhealthy,
                [HealthResponse.ReadyTag]))
            .Add(new HealthCheckRegistration(
                Sidecar,
                sp => new SidecarHealthCheck(sp, options.ReadinessTimeout),
                HealthStatus.Degraded,
                [HealthResponse.ReadyTag]))
            // The two roots whose exhaustion breaks a feature silently: an import that lands nowhere and
            // a transcode that dies mid-session. Both read the sampler's last reading and nothing else.
            .Add(new HealthCheckRegistration(
                LibraryStorage,
                sp => new DiskSpaceHealthCheck(LibraryStorage, sp.GetRequiredService<StorageSpaceSampler>()),
                HealthStatus.Degraded,
                [HealthResponse.ReadyTag]))
            .Add(new HealthCheckRegistration(
                TranscodeStorage,
                sp => new DiskSpaceHealthCheck(TranscodeStorage, sp.GetRequiredService<StorageSpaceSampler>()),
                HealthStatus.Degraded,
                [HealthResponse.ReadyTag]));

        return services;
    }

    /// <summary>
    /// The roots this composition actually has. Resolved optionally, like the sidecar probe: they come
    /// from the production adapters, and a composition registered without them — the wiring tests, an
    /// installation that never imports — has no root to watch and must report nothing rather than a
    /// failure.
    /// </summary>
    private static IEnumerable<StorageRoot> WatchedRoots(IServiceProvider services, long minimumFreeBytes)
    {
        var candidates = new (string Name, string? Path)[]
        {
            (LibraryStorage, services.GetService<ImportOptions>()?.LibraryRoot),
            (TranscodeStorage, services.GetService<PlaybackOptions>()?.TranscodeRoot),
        };

        return candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Path))
            .Select(candidate => new StorageRoot(candidate.Name, candidate.Path!, minimumFreeBytes));
    }
}
