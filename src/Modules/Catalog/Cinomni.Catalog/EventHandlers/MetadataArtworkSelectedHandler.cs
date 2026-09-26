using Cinomni.Catalog.Messaging;
using Cinomni.Kernel.Messaging;
using Cinomni.Metadata.Contracts;
using Cinomni.Operations.Messaging;

namespace Cinomni.Catalog.EventHandlers;

/// <summary>
/// Reacts to Metadata's <see cref="MetadataArtworkSelected"/> by re-pointing the work's selected artwork.
/// Enqueues a command (the handler runs in the outbox relay's transaction, so the catalog write happens
/// in its own unit of work); idempotent by the event id (<c>update-work-artwork:{eventId}</c>), which is
/// stable across redeliveries yet unique per selection — a value-based key would drop a re-pick back to a
/// previously used url. Catalog owns and writes its own tables — Metadata never touches them (Metadata ✗ Catalog).
/// </summary>
public sealed class MetadataArtworkSelectedHandler(ICommandQueue commandQueue) : IEventHandler<MetadataArtworkSelected>
{
    public async Task HandleAsync(MetadataArtworkSelected domainEvent, CancellationToken cancellationToken = default) =>
        await commandQueue.EnqueueAsync(
            new UpdateWorkArtworkCommand(domainEvent.WorkId, domainEvent.PosterUrl, domainEvent.BackdropUrl),
            idempotencyKey: $"update-work-artwork:{domainEvent.EventId}",
            cancellationToken);
}
