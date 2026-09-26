using Cinomni.Catalog.Messaging;
using Cinomni.Import.Contracts;
using Cinomni.Kernel.Messaging;
using Cinomni.Operations.Messaging;

namespace Cinomni.Catalog.EventHandlers;

/// <summary>
/// Reacts to Import's <see cref="MediaAvailable"/> by marking the catalog units the asset covers
/// available. Enqueues commands only (the handler runs in the outbox relay's transaction, so every
/// catalog write happens in its own unit of work). Catalog owns and writes its own tables — Library never
/// touches them: Library reaches Catalog through an event, never a write.
/// <para>
/// Running inside the relay's transaction also means this handler <b>cannot query the database</b>: it
/// cannot ask whether the work is a movie or a series. It does not have to. Import publishes
/// <c>UnitIds = [WorkId]</c> for a movie and the episode ids for a series, so the shape of the payload is
/// the discriminator: units that are not the work itself are episodes and fan out one command each.
/// </para>
/// </summary>
public sealed class MediaAvailableHandler(ICommandQueue commandQueue) : IEventHandler<MediaAvailable>
{
    public async Task HandleAsync(MediaAvailable domainEvent, CancellationToken cancellationToken = default)
    {
        var targetId = domainEvent.TargetIds.Count > 0 ? domainEvent.TargetIds[0] : Guid.Empty;

        var episodeIds = (domainEvent.UnitIds ?? [])
            .Where(unitId => unitId != domainEvent.WorkId && unitId != Guid.Empty)
            .Distinct()
            .ToList();

        // The movie path, bit-for-bit what it always was: one command, keyed on the asset. A pre-series
        // row with no UnitIds at all deserializes into exactly this branch too.
        if (episodeIds.Count == 0)
        {
            await commandQueue.EnqueueAsync(
                new MarkWorkAvailableCommand(domainEvent.WorkId, targetId),
                idempotencyKey: $"mark-work-available:{domainEvent.AssetId}",
                cancellationToken);
            return;
        }

        // One command per unit, each with its own key: keying the fan-out on the asset alone would
        // deduplicate every episode of a season pack but the first.
        foreach (var episodeId in episodeIds)
        {
            await commandQueue.EnqueueAsync(
                new MarkEpisodeAvailableCommand(episodeId, domainEvent.AssetId, targetId),
                idempotencyKey: $"mark-episode-available:{domainEvent.AssetId}:{episodeId}",
                cancellationToken);
        }
    }
}
