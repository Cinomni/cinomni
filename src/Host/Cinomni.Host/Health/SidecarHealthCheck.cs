using Cinomni.Downloads.Engine;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Cinomni.Host.Health;

/// <summary>
/// Whether the torrent engine in use — the libtorrent sidecar, or an external qBittorrent when one is
/// configured — is reachable. Each engine registration provides its own <see cref="IDownloadEngineProbe"/>,
/// so the check asks the engine that will actually receive the downloads.
/// <para>
/// Degraded, never Unhealthy. A household installation with no torrent engine still browses its
/// library, plays what it already has, imports nothing new and says so — that is a reduced
/// installation, not a broken one, and answering 503 for it would take the whole node out of rotation
/// over a feature. It is also the normal state of a development checkout, where no sidecar runs at all.
/// </para>
/// <para>
/// The probe is optional in the container: a composition without a production engine registered — the
/// wiring tests, and any deployment that never enables downloads — reports healthy rather than failing
/// to resolve.
/// </para>
/// </summary>
internal sealed class SidecarHealthCheck(IServiceProvider services, TimeSpan timeout) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (services.GetService<IDownloadEngineProbe>() is not { } probe)
        {
            return HealthCheckResult.Healthy();
        }

        return await probe.IsReachableAsync(timeout, cancellationToken)
            ? HealthCheckResult.Healthy()
            // No description: it would name the engine's address to an unauthenticated caller.
            : HealthCheckResult.Degraded();
    }
}
