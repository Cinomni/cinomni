using Cinomni.Kernel.Messaging;
using Cinomni.Operations.Messaging;
using Cinomni.Requests.Contracts;
using Cinomni.Requests.Messaging;

namespace Cinomni.Requests.EventHandlers;

/// <summary>
/// Reacts to this module's own <see cref="MediaRequestApproved"/> by enqueuing the fulfilment command. The
/// handler runs inside the outbox relay's transaction, so cataloguing the title (and the write that records
/// it) must happen in a command, under its own unit of work. Idempotent by <c>fulfil-request:{id}</c>.
/// </summary>
public sealed class MediaRequestApprovedHandler(ICommandQueue commandQueue) : IEventHandler<MediaRequestApproved>
{
    public async Task HandleAsync(MediaRequestApproved domainEvent, CancellationToken cancellationToken = default) =>
        await commandQueue.EnqueueAsync(
            new FulfilMediaRequestCommand(domainEvent.RequestId),
            idempotencyKey: $"fulfil-request:{domainEvent.RequestId}",
            cancellationToken);
}
