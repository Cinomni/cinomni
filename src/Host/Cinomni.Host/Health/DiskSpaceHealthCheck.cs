using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Cinomni.Host.Health;

/// <summary>
/// Whether a storage root still has room. This is the failure that actually breaks a household
/// installation: an import lands nowhere and a transcode dies mid-session, and both are far cheaper to
/// see coming than to diagnose after the fact.
/// <para>
/// The answer comes from <see cref="StorageSpaceSampler"/>'s last reading and never from a syscall — see
/// that class for why a probe that touches a filesystem is a probe that can hang for ever. This type is
/// what the health check pipeline resolves per run, so it holds no state of its own.
/// </para>
/// <para>
/// Degraded rather than Unhealthy when space runs low, and Unhealthy only when the root is gone. A full
/// disk stops new imports; it does not stop the library, playback of what is already there, or the
/// interface an operator needs in order to fix it — so it must not take the node out of rotation. A
/// root that has vanished is a different thing: it means a mount is missing.
/// </para>
/// <para>
/// The configured path is never in the response. It is a private filesystem layout, and this endpoint
/// answers an unauthenticated caller.
/// </para>
/// </summary>
/// <param name="name">The check name, which is also the sampler's key for this root.</param>
/// <param name="sampler">Holder of the last reading for every watched root.</param>
internal sealed class DiskSpaceHealthCheck(string name, StorageSpaceSampler sampler) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(sampler.Evaluate(name, DateTimeOffset.UtcNow));
}
