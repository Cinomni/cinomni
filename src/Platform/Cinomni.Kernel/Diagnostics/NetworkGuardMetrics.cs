using System.Diagnostics.Metrics;

namespace Cinomni.Kernel.Diagnostics;

/// <summary>
/// Counts outbound requests the SSRF guard refused. A misconfigured indexer, a webhook pointed at the
/// operator's own router, and an active rebinding probe are all silent today: the connection simply
/// fails and one module logs it. This turns them into a number an operator can watch.
/// <para>
/// The meter is static, and so is every other one in the product: instruments are recorded from static
/// call sites, gauge callbacks and connect callbacks that have no container and no scope, so they are
/// built directly on <see cref="CinomniTelemetry.MeterName"/> rather than through a factory. One meter
/// name for the whole monolith is what makes that safe — an exporter subscribes to the name, not to an
/// instance.
/// </para>
/// <para>
/// <b>The refused host is never a tag.</b> That value is attacker-influenced and unbounded, and for a
/// metadata or subtitle provider it is a URL that carries the API key. Only a bounded reason and the
/// module that made the call are recorded; the host stays in the exception message, which reaches a log
/// with the module's own redaction rules and never an exported time series.
/// </para>
/// </summary>
public static class NetworkGuardMetrics
{
    /// <summary>Reason values. Closed vocabulary — never an exception message.</summary>
    public static class Reasons
    {
        /// <summary>DNS resolved, but every address was private, loopback, link-local or metadata.</summary>
        public const string BlockedAddress = "blocked_address";

        /// <summary>The URL was rejected before any connection: not absolute http(s), or a blocked literal.</summary>
        public const string InvalidUrl = "invalid_url";
    }

    private static readonly Meter Meter = new(CinomniTelemetry.MeterName);

    private static readonly Counter<long> RejectionCounter = Meter.CreateCounter<long>(
        "cinomni.http_guard.rejections",
        unit: "{rejection}",
        description: "Outbound HTTP requests refused by the SSRF guard, by reason and calling module.");

    /// <summary>Records one refusal.</summary>
    /// <param name="reason">One of <see cref="Reasons"/>.</param>
    /// <param name="module">One of <see cref="CinomniTelemetry.Modules"/>.</param>
    public static void RecordRejection(string reason, string module) =>
        RejectionCounter.Add(
            1,
            new KeyValuePair<string, object?>(CinomniTelemetry.Tags.Reason, reason),
            new KeyValuePair<string, object?>(CinomniTelemetry.Tags.Module, module));
}
