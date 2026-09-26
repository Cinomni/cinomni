using System.Diagnostics;
using System.Reflection;

namespace Cinomni.Kernel.Diagnostics;

/// <summary>
/// The installation's telemetry vocabulary: one <see cref="ActivitySource"/>, one meter name, and the
/// closed set of tag keys anything in the product is allowed to attach.
/// <para>
/// There is deliberately <b>one</b> source and <b>one</b> meter for the whole monolith rather than one
/// per module. A module is a <see cref="Tags.Module"/> tag, not a separate stream: an exporter then
/// needs a single subscription, an operator's query does not have to enumerate sixteen names, and
/// nothing in a module has to be registered with the Host to become visible. It lives in the Kernel for
/// the same reason <c>SsrfGuard</c> and the authorization policies do — every module needs it and none
/// may reach another to get it.
/// </para>
/// <para>
/// <b>A span attribute is as public as a log line.</b> Telemetry leaves the process to a collector the
/// operator runs, and from there to wherever that collector forwards. Nothing below is allowed to carry
/// a secret, a token, an API key, an info hash, a file path, a release or work title, a search term, an
/// account or session identifier, a provider URL (an indexer base URL routinely embeds its API key in
/// the query string) or a raw provider payload. What is allowed is an internal opaque identifier, an
/// operator-configured name, and a bounded outcome word — see <see cref="Tags"/>.
/// </para>
/// <para>
/// With no listener attached, <see cref="ActivitySource.StartActivity(string, ActivityKind)"/> returns
/// <see langword="null"/> and every instrument recording is a flag check, so an installation that never
/// configures an exporter pays nothing for any of this.
/// </para>
/// </summary>
public static class CinomniTelemetry
{
    /// <summary>The single activity source name an exporter subscribes to.</summary>
    public const string ActivitySourceName = "Cinomni";

    /// <summary>The single meter name an exporter subscribes to.</summary>
    public const string MeterName = "Cinomni";

    /// <summary>
    /// The one activity source. Spans started here are <see langword="null"/> — and therefore free —
    /// until something registers a listener for <see cref="ActivitySourceName"/>.
    /// </summary>
    public static readonly ActivitySource Source = new(ActivitySourceName, AssemblyVersion());

    /// <summary>
    /// The closed list of tag keys. A metric label or span attribute outside this list is a review
    /// finding: it is either unbounded in cardinality, or it is data that must not leave the process.
    /// </summary>
    public static class Tags
    {
        /// <summary>Owning module, lowercase (<c>discovery</c>, <c>downloads</c>, <c>import</c>).</summary>
        public const string Module = "cinomni.module";

        /// <summary>Registered integration-event name (<c>downloads.completed</c>). Never a CLR type name.</summary>
        public const string EventName = "cinomni.event.name";

        /// <summary>Registered command name (<c>downloads.add</c>).</summary>
        public const string CommandName = "cinomni.command.name";

        /// <summary>Registered scheduled-job name (<c>operations.retention</c>).</summary>
        public const string JobName = "cinomni.job.name";

        /// <summary>
        /// Operator-configured indexer name. Bounded by how many indexers an operator adds, and chosen
        /// by that operator — unlike a base URL, which carries the API key.
        /// </summary>
        public const string IndexerName = "cinomni.indexer.name";

        /// <summary>Delivery/transcode/playback method as a bounded enum name, never a URL or a path.</summary>
        public const string Method = "cinomni.method";

        /// <summary>Bounded outcome word: <c>completed</c>, <c>retried</c>, <c>failed</c>, <c>dropped</c>.</summary>
        public const string Outcome = "cinomni.outcome";

        /// <summary>
        /// Bounded machine-readable reason for a refusal. A closed vocabulary defined by the site that
        /// records it — never an exception message, which can quote the input it rejected.
        /// </summary>
        public const string Reason = "cinomni.reason";

        /// <summary>Command-queue state (<c>Queued</c>, <c>Running</c>, <c>Completed</c>, <c>Failed</c>).</summary>
        public const string State = "cinomni.state";

        /// <summary>Transfer direction: <c>down</c> or <c>up</c>.</summary>
        public const string Direction = "cinomni.direction";

        /// <summary>
        /// An internal opaque identifier. <b>Spans only</b> — putting an id on a metric would make its
        /// cardinality unbounded. Use it to answer "which acquisition was this", never to name a title.
        /// </summary>
        public const string EntityId = "cinomni.entity.id";
    }

    /// <summary>
    /// The values <see cref="Tags.Outcome"/> may take across the whole product. Closed and shared for
    /// the same reason the tag keys are: an outcome spelled <c>failed</c> in one module and
    /// <c>Failed</c> in another is two series where an operator expects one, and nothing fails to
    /// build when it happens.
    /// <para>
    /// A module whose terminal states are genuinely its own — Import ends registered, unmatched,
    /// rejected or deferred — declares them beside its instruments instead. This is the vocabulary for
    /// work that simply succeeded, failed, will be tried again, or was never done at all.
    /// </para>
    /// </summary>
    public static class Outcomes
    {
        /// <summary>The work ran and succeeded.</summary>
        public const string Completed = "completed";

        /// <summary>It ran and failed, and will not be tried again by itself.</summary>
        public const string Failed = "failed";

        /// <summary>It failed and is scheduled to be tried again.</summary>
        public const string Retried = "retried";

        /// <summary>It was never done: an idempotency key was already spent, so nothing ran.</summary>
        public const string Dropped = "dropped";
    }

    /// <summary>Module tag values, so a typo cannot fragment a metric across two spellings.</summary>
    public static class Modules
    {
        public const string Operations = "operations";
        public const string Discovery = "discovery";
        public const string Downloads = "downloads";
        public const string Import = "import";
        public const string Playback = "playback";
        public const string Metadata = "metadata";
        public const string Notifications = "notifications";
        public const string Subtitles = "subtitles";
    }

    private static string AssemblyVersion() =>
        typeof(CinomniTelemetry).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(CinomniTelemetry).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";
}
