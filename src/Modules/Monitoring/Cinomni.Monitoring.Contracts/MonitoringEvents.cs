using Cinomni.Kernel.Messaging;

namespace Cinomni.Monitoring.Contracts;

/// <summary>Stable registered names of the Monitoring integration events.</summary>
public static class MonitoringEventNames
{
    public const string MonitoringEnabled = "monitoring.monitoring-enabled";
    public const string SearchRequested = "monitoring.search-requested";
    public const string MonitoringRemoved = "monitoring.monitoring-removed";
}

/// <summary>
/// Monitoring was enabled for a target. Published atomically with the target write; consumed
/// later by Notifications. Ids travel as raw <see cref="Guid"/> for wire stability.
/// </summary>
/// <param name="Kind">
/// The target's kind (<c>Movie</c>, <c>Series</c>, <c>Season</c>, <c>Episode</c>) as text, so a
/// consumer can tell a policy root from an acquirable leaf without querying Monitoring. Trailing
/// optional: an in-flight 1.x jsonb row deserializes with it null, which means "assume acquirable".
/// </param>
/// <param name="UnitId">
/// The catalog unit the target watches (the work id for a movie, the season/episode id for a
/// series leaf). Trailing optional for the same wire-stability reason.
/// </param>
public sealed record MonitoringEnabled(
    Guid TargetId,
    Guid WorkId,
    string Mode,
    string? Kind = null,
    Guid? UnitId = null) : DomainEvent
{
    // Keyed by target + instant so a disable→re-enable cycle produces a distinct event, while a
    // redelivery of the same instance keeps its key (OccurredAt is fixed at construction and
    // travels in the payload). Without the instant, a consumer would drop a genuine re-enable.
    public override string IdempotencyKey => $"monitoring-enabled:{TargetId}:{OccurredAt.UtcTicks}";
}

/// <summary>
/// A work was removed from the catalog and Monitoring has deleted every target it held for it. Published
/// atomically with the deletion, so a consumer that only knows targets (Acquisition) can let go of the
/// work without depending on Catalog. Carries the administrator's choice about the files onward.
/// </summary>
/// <param name="DeleteFiles">Whether the files on disk should be deleted too.</param>
public sealed record MonitoringRemoved(Guid WorkId, bool DeleteFiles) : DomainEvent
{
    public override string IdempotencyKey => $"monitoring-removed:{WorkId}";
}

// SearchRequested lives in Cinomni.Search.Contracts (neutral, no Catalog dependency) so
// Discovery can consume it without transitively referencing Catalog. Its registered name stays
// MonitoringEventNames.SearchRequested — Monitoring is the producer.
