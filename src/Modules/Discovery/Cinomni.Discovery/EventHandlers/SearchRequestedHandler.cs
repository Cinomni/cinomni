using Cinomni.Discovery.Messaging;
using Cinomni.Kernel.Messaging;
using Cinomni.Operations.Messaging;
using Cinomni.Search.Contracts;
using Microsoft.Extensions.Logging;

namespace Cinomni.Discovery.EventHandlers;

/// <summary>
/// Reacts to Monitoring's <see cref="SearchRequested"/> by enqueuing an
/// <see cref="ExecuteSearchCommand"/>. It enqueues rather than searching inline: this handler
/// runs inside the outbox relay's transaction (fast HTTP fan-out must not happen there), and the
/// command queue gives the search recoverable, backed-off execution.
/// <para>
/// Idempotent per target and <see cref="SearchRequested.Window"/> — one <em>search occasion</em>. The
/// key is spent for ever, so a drop here means no indexer is ever asked for that occasion; it can only
/// legitimately happen on a redelivery of the same event, and it is logged either way.
/// </para>
/// </summary>
public sealed class SearchRequestedHandler(
    ICommandQueue commandQueue,
    ILogger<SearchRequestedHandler> logger)
    : IEventHandler<SearchRequested>
{
    public async Task HandleAsync(SearchRequested domainEvent, CancellationToken cancellationToken = default)
    {
        var enqueued = await commandQueue.EnqueueAsync(
            new ExecuteSearchCommand(
                domainEvent.Criterion, domainEvent.TargetId, domainEvent.WorkId, domainEvent.UnitIds),
            idempotencyKey: $"execute-search:{domainEvent.TargetId}:{domainEvent.Window}",
            cancellationToken);

        if (!enqueued)
        {
            logger.LogInformation(
                "Search for target {TargetId} in window {Window} was already queued; no indexer will be "
                + "asked again for that occasion.",
                domainEvent.TargetId,
                domainEvent.Window);
        }
    }
}
