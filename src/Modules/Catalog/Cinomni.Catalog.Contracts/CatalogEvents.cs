using Cinomni.Kernel.Messaging;

namespace Cinomni.Catalog.Contracts;

/// <summary>Stable registered names of the Catalog integration events.</summary>
public static class CatalogEventNames
{
    public const string WorkAdded = "catalog.work-added";
    public const string WorkRemoved = "catalog.work-removed";
    public const string WorkAvailable = "catalog.work-available";
    public const string SeriesStructureChanged = "catalog.series-structure-changed";
    public const string EpisodeAvailable = "catalog.episode-available";
}

/// <summary>
/// A work was added to the catalog. Published atomically with the work write. Lives in the
/// contracts assembly (not the module) so consumers such as Monitoring can react to it without
/// referencing Catalog internals. <paramref name="Monitored"/> is whether
/// whoever added it wants it looked for; false for a title that arrived from a list rather than from a
/// person. It defaults to true so a message written before the field existed still reads as it meant.
/// </summary>
public sealed record WorkAdded(Guid WorkId, string Kind, string Title, int? Year, bool Monitored = true) : DomainEvent
{
    public override string IdempotencyKey => $"work-added:{WorkId}";
}

/// <summary>
/// An administrator removed a work from the catalog. Published atomically with the removal. Every module
/// that holds something for the work reacts on its own (eventual, not transactional): Monitoring stops
/// watching it, Acquisition cancels its goals, Downloads drops its torrents, Library retires its assets
/// and — only when <paramref name="DeleteFiles"/> is true — the downloaded and imported files are deleted.
/// </summary>
/// <param name="DeleteFiles">Whether the administrator also asked for the files on disk to go.</param>
public sealed record WorkRemoved(Guid WorkId, string Kind, string Title, bool DeleteFiles) : DomainEvent
{
    public override string IdempotencyKey => $"work-removed:{WorkId}";
}

/// <summary>
/// A work now has a playable asset (Catalog reacted to Import's <c>MediaAvailable</c> and marked it
/// available). Consumed by Monitoring (stop searching) and Notifications. Keyed by the work.
/// </summary>
public sealed record WorkAvailable(Guid WorkId, Guid TargetId) : DomainEvent
{
    public override string IdempotencyKey => $"work-available:{WorkId}";
}

/// <summary>
/// A series gained (or had refreshed) its season/episode structure from a provider snapshot.
/// Consumed by Monitoring to materialise season and episode targets.
/// <para>
/// Keyed by <c>(work, snapshot)</c> rather than by the work alone: idempotency keys are consumed
/// forever, and a series legitimately grows a new season with every later snapshot — keying on the
/// work would silently swallow every one of them.
/// </para>
/// <para>
/// The <c>Created*</c> lists carry the rows this sync actually inserted; a re-run of the same
/// snapshot reports empty lists with unchanged totals. Payload members are plain
/// <see cref="Guid"/>/<see cref="int"/> for wire stability — this record is persisted as jsonb in
/// <c>operations.outbox</c>, so members may only ever be APPENDED with a default.
/// </para>
/// </summary>
public sealed record SeriesStructureChanged(
    Guid WorkId,
    Guid SnapshotId,
    string Provider,
    int SeasonCount,
    int EpisodeCount,
    IReadOnlyList<Guid> CreatedSeasonIds,
    IReadOnlyList<Guid> CreatedEpisodeIds) : DomainEvent
{
    public override string IdempotencyKey => $"series-structure:{WorkId}:{SnapshotId}";
}

/// <summary>
/// A single episode now has a playable asset. Published once per episode, so a season-pack import
/// fans out to N facts that each deduplicate independently — keying on the asset instead would
/// collapse the whole pack into one.
/// </summary>
public sealed record EpisodeAvailable(
    Guid WorkId,
    Guid SeasonId,
    Guid EpisodeId,
    int SeasonNumber,
    int EpisodeNumber,
    Guid AssetId,
    Guid TargetId) : DomainEvent
{
    public override string IdempotencyKey => $"episode-available:{EpisodeId}";
}
