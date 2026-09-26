using Cinomni.Kernel.Messaging;

namespace Cinomni.Metadata.Contracts;

/// <summary>Stable registered names of the Metadata integration events.</summary>
public static class MetadataEventNames
{
    public const string MetadataRefreshed = "metadata.refreshed";
    public const string MetadataRefreshFailed = "metadata.refresh-failed";
    public const string MetadataArtworkSelected = "metadata.artwork-selected";
    public const string ProviderDegraded = "metadata.provider-degraded";
}

/// <summary>
/// A fresh metadata snapshot was obtained for a work. Consumed by Catalog, which copies the snapshot's
/// fields onto its own work (the ACL — identity stays internal). Keyed by the snapshot.
/// </summary>
public sealed record MetadataRefreshed(Guid WorkId, Guid SnapshotId, string Provider) : DomainEvent
{
    public override string IdempotencyKey => $"metadata-refreshed:{SnapshotId}";
}

/// <summary>
/// A metadata refresh failed (provider down / rate-limited). The work persists without a fresh snapshot
/// and the refresh backs off. Keyed by work+provider+attempt.
/// </summary>
public sealed record MetadataRefreshFailed(Guid WorkId, string Provider, int Attempt, string Reason) : DomainEvent
{
    public override string IdempotencyKey => $"metadata-refresh-failed:{WorkId}:{Provider}:{Attempt}";
}

/// <summary>
/// The selected artwork of a snapshot changed (an operator re-picked a poster/backdrop, or the first
/// selection landed). Consumed by Catalog to update just the work's artwork — a lighter follow-up to
/// <see cref="MetadataRefreshed"/> that does not re-copy the descriptive fields. Keyed by the event id
/// (assigned once, stable across redeliveries) so a genuine re-pick — even back to a previously selected
/// url — is a distinct fact, while an at-least-once redelivery of the same emission still deduplicates.
/// A value-based key would make re-picking a prior artwork a permanent no-op downstream.
/// </summary>
public sealed record MetadataArtworkSelected(Guid WorkId, Guid SnapshotId, string? PosterUrl, string? BackdropUrl) : DomainEvent
{
    public override string IdempotencyKey => $"metadata-artwork-selected:{EventId}";
}

/// <summary>
/// A provider has repeatedly failed for a work and crossed the degradation threshold — a signal for
/// observability/alerting that this provider is unhealthy (distinct from the per-attempt
/// <see cref="MetadataRefreshFailed"/>). Keyed by work+provider+attempt so each crossing is announced once.
/// </summary>
public sealed record ProviderDegraded(Guid WorkId, string Provider, int Attempt, string Reason) : DomainEvent
{
    public override string IdempotencyKey => $"metadata-provider-degraded:{WorkId}:{Provider}:{Attempt}";
}
