using System.Diagnostics;
using System.Diagnostics.Metrics;
using Cinomni.Kernel.Diagnostics;

namespace Cinomni.Discovery.Diagnostics;

/// <summary>
/// How each indexer is actually behaving. A federated search swallows a failing indexer by design —
/// one bad endpoint must not fail the whole search — which means an indexer that has been returning
/// nothing for a week looks exactly like a title nobody is seeding. These numbers are the difference.
/// <para>
/// The indexer name is the one tag, and it is safe: it is chosen by the operator when they add the
/// indexer, and it is bounded by how many they add. The base URL is <b>not</b> a tag and never will be
/// — a Torznab base URL carries the account's API key in its query string.
/// </para>
/// <para>
/// Neither is the search term, the release title, or the work being searched for. What the household
/// is looking for is nobody's business, including a metrics backend's.
/// </para>
/// </summary>
public static class DiscoveryMetrics
{
    private static readonly Meter Meter = new(CinomniTelemetry.MeterName);

    private static readonly KeyValuePair<string, object?> ModuleTag =
        new(CinomniTelemetry.Tags.Module, CinomniTelemetry.Modules.Discovery);

    private static readonly Histogram<double> SearchDuration = Meter.CreateHistogram<double>(
        "cinomni.indexer.search.duration",
        unit: "s",
        description: "Time one indexer took to answer a search, successful or not.");

    private static readonly Counter<long> SearchFailures = Meter.CreateCounter<long>(
        "cinomni.indexer.search.failures",
        unit: "{search}",
        description: "Indexer searches that threw and returned no candidates.");

    private static readonly Counter<long> SearchResults = Meter.CreateCounter<long>(
        "cinomni.indexer.search.results",
        unit: "{candidate}",
        description: "Candidates an indexer returned, before cross-indexer deduplication.");

    private static readonly Counter<long> Logins = Meter.CreateCounter<long>(
        "cinomni.indexer.logins",
        unit: "{login}",
        description: "Login sequences a definition-backed indexer completed and kept as its session.");

    private static readonly Counter<long> Relogins = Meter.CreateCounter<long>(
        "cinomni.indexer.relogins",
        unit: "{login}",
        description: "Login sequences re-run because a search response said the site had expired the session.");

    private static readonly Counter<long> LoginFailures = Meter.CreateCounter<long>(
        "cinomni.indexer.login_failures",
        unit: "{login}",
        description: "Login sequences that produced no session; the indexer is searched unauthenticated until one succeeds.");

    /// <summary>Records one indexer's answer.</summary>
    /// <param name="indexerName">The operator-chosen name. Never the base URL.</param>
    /// <param name="duration">Wall time for that one call.</param>
    /// <param name="candidateCount">How many candidates came back.</param>
    public static void RecordSearch(string indexerName, TimeSpan duration, int candidateCount)
    {
        var tags = Tags(indexerName, succeeded: true);
        SearchDuration.Record(duration.TotalSeconds, tags);
        SearchResults.Add(candidateCount, tags);
    }

    /// <summary>
    /// Records an indexer that failed. The exception is not passed in on purpose: its message quotes
    /// the request that produced it, which is the URL with the key in it.
    /// </summary>
    public static void RecordSearchFailure(string indexerName, TimeSpan duration)
    {
        var tags = Tags(indexerName, succeeded: false);
        SearchDuration.Record(duration.TotalSeconds, tags);
        SearchFailures.Add(1, tags);
    }

    /// <summary>Records one completed sign-in whose cookies were kept.</summary>
    public static void RecordLogin(string indexerName) => Logins.Add(1, Tags(indexerName, succeeded: true));

    /// <summary>Records a sign-in re-run after a search response showed the stored session had expired.</summary>
    public static void RecordRelogin(string indexerName) => Relogins.Add(1, Tags(indexerName, succeeded: true));

    /// <summary>
    /// Records a sign-in that produced no session. Like every recorder here, it takes a name and no
    /// exception: a login failure's message can quote the submitted URL or a Set-Cookie value.
    /// </summary>
    public static void RecordLoginFailure(string indexerName) => LoginFailures.Add(1, Tags(indexerName, succeeded: false));

    private static TagList Tags(string indexerName, bool succeeded) => new()
    {
        ModuleTag,
        new KeyValuePair<string, object?>(CinomniTelemetry.Tags.IndexerName, indexerName),
        new KeyValuePair<string, object?>(
            CinomniTelemetry.Tags.Outcome,
            succeeded ? CinomniTelemetry.Outcomes.Completed : CinomniTelemetry.Outcomes.Failed),
    };
}
