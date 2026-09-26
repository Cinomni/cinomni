using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Cinomni.Host.Health;

/// <summary>
/// One readiness answer, shared by everyone who asks for it inside a short window.
/// <para>
/// <b>Why a probe may not be an amplifier.</b> <c>/health</c> and <c>/health/ready</c> are anonymous,
/// unauthenticated and unrated — an orchestrator has no credentials, so they have to be. Running the
/// checks per request meant a caller who can reach the port could turn a loop of cheap GETs into one
/// pooled PostgreSQL connection and one outbound gRPC connect each, every one of them parked for the
/// full readiness budget while the sidecar is down. A few hundred of those exhaust the connection pool
/// the acquisition spine is using and starve the real workload.
/// </para>
/// <para>
/// So the dependencies are asked at most once per <see cref="DefaultMaxAge"/>, and at most once at a
/// time: concurrent callers wait for the answer already being computed rather than starting another.
/// Probe traffic is then bounded work no matter how much of it arrives, and the answer is at most one
/// poll old — a readiness signal is a level, not an event.
/// </para>
/// </summary>
/// <param name="health">The framework's check runner, over the checks tagged for readiness.</param>
/// <param name="maxAge">How long an answer may be reused.</param>
internal sealed class ReadinessSnapshot(HealthCheckService health, TimeSpan maxAge)
{
    /// <summary>
    /// Short enough that an operator watching a dependency come back does not think it is stuck, long
    /// enough that a flood of probes collapses into one check.
    /// </summary>
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The liveness answer: no checks, no entries, no dependency. The process replying <b>is</b> the
    /// measurement, which is the whole reason liveness and readiness are two probes.
    /// </summary>
    public static readonly HealthReport Alive = new(
        new Dictionary<string, HealthReportEntry>(StringComparer.Ordinal),
        HealthStatus.Healthy,
        TimeSpan.Zero);

    private readonly SemaphoreSlim _gate = new(1, 1);

    private Taken? _last;

    /// <summary>The checks readiness runs. Liveness runs none of them.</summary>
    public static bool IsReadinessCheck(HealthCheckRegistration check) =>
        check.Tags.Contains(HealthResponse.ReadyTag);

    /// <summary>Returns the last answer, or computes one if it has aged out.</summary>
    /// <param name="cancellationToken">The caller's token. It abandons the wait, never the shared work.</param>
    public async Task<HealthReport> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (Fresh() is { } cached)
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Somebody else may have refreshed it while this caller waited, which is the point of the
            // gate: one dependency check serves everyone queued behind it.
            if (Fresh() is { } refreshed)
            {
                return refreshed;
            }

            // Deliberately not the request's token. The work is shared, so one impatient caller hanging
            // up must not cancel the answer the others are waiting for; each check carries its own
            // budget, which is what bounds this.
            var report = await health.CheckHealthAsync(IsReadinessCheck, CancellationToken.None);
            Volatile.Write(ref _last, new Taken(report, DateTimeOffset.UtcNow));
            return report;
        }
        finally
        {
            _gate.Release();
        }
    }

    private HealthReport? Fresh()
    {
        // One reference read: the report and the moment it was taken travel together, so a reader can
        // never pair a fresh timestamp with a stale report.
        var last = Volatile.Read(ref _last);
        return last is not null && DateTimeOffset.UtcNow - last.TakenAt <= maxAge ? last.Report : null;
    }

    private sealed record Taken(HealthReport Report, DateTimeOffset TakenAt);
}
