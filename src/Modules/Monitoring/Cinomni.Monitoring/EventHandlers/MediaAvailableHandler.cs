using Cinomni.Import.Contracts;
using Cinomni.Kernel.Messaging;
using Cinomni.Monitoring.Messaging;
using Cinomni.Operations.Messaging;

namespace Cinomni.Monitoring.EventHandlers;

/// <summary>
/// Reacts to Import's <see cref="MediaAvailable"/> by clearing the missing flag of the targets watching
/// the catalog units the asset covers.
/// <para>
/// Nothing in the repository used to do this, which is a live defect on the movie path too: a monitored
/// target stayed <c>IsMissing</c> forever and was re-searched every six hours for the rest of its life.
/// At episode granularity it would be unmanageable.
/// </para>
/// <para>
/// Enqueues a command only — this handler runs inside the outbox relay's transaction, so it may not write
/// Monitoring's tables. Keyed <c>units-satisfied:{assetId}</c>: an asset lands once, and a
/// redelivery must not re-run the rollup.
/// </para>
/// </summary>
public sealed class MediaAvailableHandler(ICommandQueue commandQueue) : IEventHandler<MediaAvailable>
{
    public Task HandleAsync(MediaAvailable domainEvent, CancellationToken cancellationToken = default)
    {
        // A pre-series (1.x) row carries no UnitIds at all; for a movie the unit always was the work.
        var units = (domainEvent.UnitIds ?? [])
            .Where(unitId => unitId != Guid.Empty)
            .Distinct()
            .ToList();

        if (units.Count == 0)
        {
            units = [domainEvent.WorkId];
        }

        return commandQueue.EnqueueAsync(
            new MarkUnitsSatisfiedCommand(domainEvent.WorkId, domainEvent.AssetId, units),
            idempotencyKey: $"units-satisfied:{domainEvent.AssetId}",
            cancellationToken);
    }
}
