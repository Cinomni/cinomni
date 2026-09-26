using Cinomni.Kernel.Messaging;

namespace Cinomni.Catalog.Messaging;

/// <summary>Stable registered names of the Catalog commands.</summary>
public static class CatalogCommandNames
{
    public const string MarkWorkAvailable = "catalog.mark-work-available";
    public const string MarkEpisodeAvailable = "catalog.mark-episode-available";
    public const string AttachMetadataSnapshot = "catalog.attach-metadata-snapshot";
    public const string SyncSeriesStructure = "catalog.sync-series-structure";
    public const string UpdateWorkArtwork = "catalog.update-work-artwork";
    public const string RefreshTrendingList = "catalog.refresh-trending-list";
    public const string SweepCollectionPlacement = "catalog.sweep-collection-placement";
}

/// <summary>
/// Periodic, opt-in sweep that adds titles from the trending list. Parameterless so the scheduler can
/// construct it. The handler no-ops when the setting is off, and a redelivery adds nothing twice.
/// </summary>
public sealed record RefreshTrendingListCommand : ICommand;

/// <summary>
/// Marks a work available — enqueued by Import's <c>MediaAvailable</c> reaction. Idempotent by
/// <c>mark-work-available:{assetId}</c>.
/// </summary>
public sealed record MarkWorkAvailableCommand(Guid WorkId, Guid TargetId) : ICommand;

/// <summary>
/// Marks one episode available — enqueued once per catalog unit an import landed content for, so a
/// season-pack fan-out becomes N independent commands. Idempotent by
/// <c>mark-episode-available:{assetId}:{episodeId}</c>: keying on the asset alone would collapse the whole
/// pack into a single command and only the first episode would ever be marked.
/// </summary>
public sealed record MarkEpisodeAvailableCommand(Guid EpisodeId, Guid AssetId, Guid TargetId) : ICommand;

/// <summary>
/// Attaches a metadata snapshot to a work — enqueued by Metadata's <c>MetadataRefreshed</c> reaction. The
/// handler reads the snapshot from Metadata (→i) and copies its fields (including selected artwork) onto
/// the work. Idempotent by <c>attach-metadata:{snapshotId}</c>.
/// </summary>
public sealed record AttachMetadataSnapshotCommand(Guid WorkId, Guid SnapshotId) : ICommand;

/// <summary>
/// Materialises a snapshot's season/episode tree onto a series — enqueued by Metadata's
/// <c>MetadataRefreshed</c> reaction.
/// <para>
/// Carries <b>pointers only</b>. The handler reads the tree back through
/// <c>IMetadataQuery.GetSeriesStructureAsync</c>; command payloads are persisted as jsonb in
/// <c>operations.command</c>, so shipping the 900 episodes of a long-running series through the queue
/// would be megabytes per refresh.
/// </para>
/// <para>
/// Idempotent by <c>sync-series-structure:{snapshotId}</c> — never by the work. Idempotency keys are
/// consumed forever (<c>ux_command_idempotency_key</c> has no cleanup), and a series legitimately
/// re-syncs whenever a new season airs, so a work-keyed command would silently swallow every later
/// snapshot.
/// </para>
/// </summary>
public sealed record SyncSeriesStructureCommand(Guid WorkId, Guid SnapshotId) : ICommand;

/// <summary>
/// Re-points a work's selected artwork — enqueued by Metadata's <c>MetadataArtworkSelected</c> reaction.
/// Carries the urls directly (small payload). Idempotent by <c>update-work-artwork:{eventId}</c> (stable
/// across redeliveries, unique per selection).
/// </summary>
public sealed record UpdateWorkArtworkCommand(Guid WorkId, string? PosterUrl, string? BackdropUrl) : ICommand;

/// <summary>
/// Re-places one batch of works under the current collection rules, then queues the batch after it —
/// enqueued in the same unit of work as the rule or order change that needs it. The cursor is the last
/// work id handled, so the chain resumes where it stopped after a restart. Idempotent by
/// <c>collection-sweep:{RunId}:{After}</c>; a batch run twice moves nothing the second time.
/// </summary>
public sealed record SweepCollectionPlacementCommand(Guid RunId, Guid? After) : ICommand;
