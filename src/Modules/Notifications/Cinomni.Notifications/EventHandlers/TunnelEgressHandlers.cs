using Cinomni.Downloads.Contracts;
using Cinomni.Kernel.Messaging;
using Cinomni.Notifications.Messaging;
using Cinomni.Operations.Messaging;

namespace Cinomni.Notifications.EventHandlers;

/// <summary>
/// Reacts to Downloads' <see cref="TunnelEgressLost"/> by telling the operator that transfers were
/// stopped, and why. Enqueues a command rather than writing here, because the handler runs inside the
/// outbox relay's transaction.
/// <para>
/// Keyed by the transition sequence, which is what makes it correct rather than merely idempotent:
/// one outage raises one notification however many times the event is redelivered, and a second
/// outage — a genuinely new fact — raises its own.
/// </para>
/// </summary>
public sealed class TunnelEgressLostHandler(ICommandQueue commandQueue) : IEventHandler<TunnelEgressLost>
{
    public async Task HandleAsync(TunnelEgressLost domainEvent, CancellationToken cancellationToken = default)
    {
        var dedupKey = $"tunnel-egress-lost:{domainEvent.Sequence}";
        await commandQueue.EnqueueAsync(
            new RaiseNotificationCommand(
                NotificationKind.TunnelEgressLost,
                WorkId: null,
                Detail: domainEvent.Reason,
                DedupKey: dedupKey),
            idempotencyKey: $"notify:{dedupKey}",
            cancellationToken);
    }
}

/// <summary>The counterpart: the tunnel is carrying download traffic again and the holds are lifted.</summary>
public sealed class TunnelEgressRestoredHandler(ICommandQueue commandQueue) : IEventHandler<TunnelEgressRestored>
{
    public async Task HandleAsync(TunnelEgressRestored domainEvent, CancellationToken cancellationToken = default)
    {
        var dedupKey = $"tunnel-egress-restored:{domainEvent.Sequence}";
        await commandQueue.EnqueueAsync(
            new RaiseNotificationCommand(
                NotificationKind.TunnelEgressRestored,
                WorkId: null,
                Detail: null,
                DedupKey: dedupKey),
            idempotencyKey: $"notify:{dedupKey}",
            cancellationToken);
    }
}
