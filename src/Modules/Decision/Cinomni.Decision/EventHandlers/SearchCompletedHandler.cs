using Cinomni.Decision.Messaging;
using Cinomni.Discovery.Contracts;
using Cinomni.Kernel.Messaging;
using Cinomni.Operations.Messaging;

namespace Cinomni.Decision.EventHandlers;

/// <summary>
/// Reacts to Discovery's <see cref="SearchCompleted"/> by enqueuing an evaluation. It enqueues
/// rather than evaluating inline: this handler runs inside the outbox relay's transaction, and the
/// command queue gives the evaluation recoverable, backed-off execution. Idempotent per search.
/// </summary>
public sealed class SearchCompletedHandler(ICommandQueue commandQueue) : IEventHandler<SearchCompleted>
{
    public async Task HandleAsync(SearchCompleted domainEvent, CancellationToken cancellationToken = default)
    {
        // Nothing was found — nothing to evaluate. Skipping keeps the empty-search case off the
        // evaluation path entirely (and out of the 0-candidate idempotency edge).
        if (domainEvent.ResultCount <= 0)
        {
            return;
        }

        await commandQueue.EnqueueAsync(
            new EvaluateReleasesCommand(domainEvent.SearchId, domainEvent.TargetId),
            idempotencyKey: $"evaluate-releases:{domainEvent.SearchId}",
            cancellationToken);
    }
}
