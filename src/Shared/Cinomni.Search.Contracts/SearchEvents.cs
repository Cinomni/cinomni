using System.Globalization;
using Cinomni.Kernel.Messaging;

namespace Cinomni.Search.Contracts;

/// <summary>
/// A search is requested for a target (Monitoring → Discovery). Drives the recurring search cadence
/// (new RSS items, missing targets, targets below their quality cutoff). Carries a neutral <see cref="SearchCriterion"/> that Monitoring
/// builds from the work, so Discovery never has to know the Work (it may not reference Catalog).
/// The <paramref name="Window"/> names the <b>search occasion</b> so a consumer can collapse a
/// redelivery of this very event without collapsing the next, distinct request for the same target.
/// Its registered name stays <c>monitoring.search-requested</c> (Monitoring is the producer).
/// </summary>
/// <param name="Window">
/// The occasion this request belongs to — build it with <see cref="WindowFor"/>. It is part of the
/// consumer's idempotency key, and those keys are spent for ever, so anything coarser than one
/// occasion silently caps how often a target can be searched at all.
/// </param>
/// <param name="UnitIds">
/// The catalog units this search is trying to acquire (a work id for a movie, episode ids for a
/// season pack). Trailing optional: the payload is persisted as jsonb, so an in-flight 1.x row
/// deserializes with this at its default. Null means "unknown" — treat it as the whole target.
/// </param>
public sealed record SearchRequested(
    Guid TargetId,
    Guid WorkId,
    SearchCriterion Criterion,
    string Reason,
    string Window,
    IReadOnlyList<Guid>? UnitIds = null)
    : DomainEvent
{
    /// <summary>
    /// The instant format of a search occasion: UTC, down to the tick, so two decisions to search the
    /// same target never share one.
    /// </summary>
    private const string WindowFormat = "yyyyMMddHHmmssfffffff";

    public override string IdempotencyKey => $"search-requested:{TargetId}:{Window}";

    /// <summary>
    /// Builds the <see cref="Window"/> for one search occasion from the instant that occasion was
    /// decided. Every request a single sweep publishes shares that instant, and a redelivery of the
    /// resulting event replays the same string — which is exactly the redelivery the consumer's
    /// idempotency key exists to collapse.
    /// <para>
    /// It used to be an hour bucket (<c>yyyyMMddHH</c>). Because the consumer keys
    /// <c>execute-search:{targetId}:{window}</c> into a table whose unique key is never cleaned up,
    /// that capped every target at one search per clock hour: the manual search trigger was a no-op
    /// for the rest of the hour and the thirty-minute just-aired retry ran at half its cadence, both
    /// silently and both while the sweep recorded the search as sent.
    /// </para>
    /// </summary>
    public static string WindowFor(DateTimeOffset occasion) =>
        occasion.UtcDateTime.ToString(WindowFormat, CultureInfo.InvariantCulture);
}
