using Cinomni.Catalog.Contracts;
using Cinomni.Kernel.Messaging;
using Cinomni.Monitoring.Messaging;
using Cinomni.Operations.Messaging;

namespace Cinomni.Monitoring.EventHandlers;

/// <summary>
/// Reacts to Catalog's <see cref="SeriesStructureChanged"/> by materialising the season and episode
/// targets of the series. Enqueues a command only — this handler runs inside the outbox relay's
/// transaction, so it may neither write Monitoring's tables nor query for anything the event did not
/// carry.
/// <para>
/// The key is <c>sync-series-targets:{workId}:{snapshotId}</c>. It must <b>not</b> be
/// <c>apply-policy:{workId}</c>: that key is permanently spent by <c>WorkAddedHandler</c> and
/// <c>ux_command_idempotency_key</c> is never cleaned up, so every later season of the show would be
/// dropped without an error or a log line.
/// </para>
/// </summary>
public sealed class SeriesStructureChangedHandler(ICommandQueue commandQueue)
    : IEventHandler<SeriesStructureChanged>
{
    public Task HandleAsync(SeriesStructureChanged domainEvent, CancellationToken cancellationToken = default) =>
        commandQueue.EnqueueAsync(
            new SyncSeriesTargetsCommand(domainEvent.WorkId, domainEvent.SnapshotId),
            idempotencyKey: $"sync-series-targets:{domainEvent.WorkId}:{domainEvent.SnapshotId}",
            cancellationToken);
}
