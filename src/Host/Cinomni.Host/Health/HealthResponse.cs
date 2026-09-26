using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Cinomni.Host.Health;

/// <summary>
/// Writes the probe response. It is deliberately tiny, because both probes are anonymous: an
/// orchestrator has no credentials, so whatever these endpoints say is said to anyone who can reach
/// the port.
/// <para>
/// The framework's default writer emits each check's <c>Description</c>, <c>Exception</c> and
/// <c>Duration</c>. On this installation a description would name a storage root, and an exception
/// would carry a connection string, a filesystem path or the sidecar's address. So this writes a
/// status and a check name and nothing else — the name is a fixed word chosen here, not a value from
/// configuration.
/// </para>
/// </summary>
internal static class HealthResponse
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Tag marking a check that readiness runs. Liveness runs none of them.</summary>
    public const string ReadyTag = "ready";

    /// <summary>
    /// Serializes a report as <c>{"status":"...","checks":[{"name":"...","status":"..."}]}</c>.
    /// <para>
    /// Degraded answers 200 on purpose. A degraded dependency — no sidecar, a storage root running low —
    /// still leaves an installation that browses, plays and serves its interface, and taking the node
    /// out of rotation for it would turn a partial outage into a total one. Only Unhealthy is 503.
    /// </para>
    /// </summary>
    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";

        // A probe answer is never cacheable. The application memoizes readiness itself, deliberately and
        // for a few seconds; a proxy or a browser holding on to it for longer would tell an orchestrator
        // that a dependency is fine long after it stopped being fine.
        context.Response.Headers.CacheControl = "no-store, no-cache";
        context.Response.Headers.Pragma = "no-cache";

        context.Response.StatusCode = report.Status == HealthStatus.Unhealthy
            ? StatusCodes.Status503ServiceUnavailable
            : StatusCodes.Status200OK;

        var payload = new HealthPayload(
            report.Status.ToString(),
            report.Entries
                .Select(entry => new HealthCheckPayload(entry.Key, entry.Value.Status.ToString()))
                .OrderBy(check => check.Name, StringComparer.Ordinal)
                .ToArray());

        return context.Response.WriteAsJsonAsync(payload, SerializerOptions);
    }

    /// <summary>The whole response body. Adding a field here is a security decision, not a formatting one.</summary>
    private sealed record HealthPayload(string Status, IReadOnlyList<HealthCheckPayload> Checks);

    private sealed record HealthCheckPayload(string Name, string Status);
}
