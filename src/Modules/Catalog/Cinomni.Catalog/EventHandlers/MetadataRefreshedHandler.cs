using Cinomni.Catalog.Messaging;
using Cinomni.Kernel.Messaging;
using Cinomni.Metadata.Contracts;
using Cinomni.Operations.Messaging;

namespace Cinomni.Catalog.EventHandlers;

/// <summary>
/// Reacts to Metadata's <see cref="MetadataRefreshed"/> by enriching the work with the new snapshot and —
/// for a series — materialising its season/episode tree. Enqueues commands only (the handler runs in the
/// outbox relay's transaction, so every catalog write happens in its own unit of work); Catalog owns and
/// writes its own tables — Metadata never touches them (Metadata ✗ Catalog).
/// <para>
/// The event does not say whether the work is a series, and the handler may not query to find out, so the
/// structure command is enqueued unconditionally: its handler no-ops when the snapshot's tree is empty,
/// which is exactly what a movie snapshot returns.
/// </para>
/// </summary>
public sealed class MetadataRefreshedHandler(ICommandQueue commandQueue) : IEventHandler<MetadataRefreshed>
{
    public async Task HandleAsync(MetadataRefreshed domainEvent, CancellationToken cancellationToken = default)
    {
        await commandQueue.EnqueueAsync(
            new AttachMetadataSnapshotCommand(domainEvent.WorkId, domainEvent.SnapshotId),
            idempotencyKey: $"attach-metadata:{domainEvent.SnapshotId}",
            cancellationToken);

        // Keyed on the snapshot, never on the work: a series re-syncs with every new season, and a
        // work-keyed idempotency key is consumed forever.
        await commandQueue.EnqueueAsync(
            new SyncSeriesStructureCommand(domainEvent.WorkId, domainEvent.SnapshotId),
            idempotencyKey: $"sync-series-structure:{domainEvent.SnapshotId}",
            cancellationToken);
    }
}
