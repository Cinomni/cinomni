using Cinomni.Acquisition.Contracts;
using Cinomni.Downloads.Messaging;
using Cinomni.Kernel.Messaging;
using Cinomni.Operations.Messaging;

namespace Cinomni.Downloads.EventHandlers;

/// <summary>
/// Reacts to Acquisition's <see cref="DownloadQueued"/> by handing the release off to the engine.
/// Enqueues a command (the engine call is an out-of-process side effect that must be retryable);
/// idempotent by <c>add-download:{attemptId}</c>.
/// </summary>
public sealed class DownloadQueuedHandler(ICommandQueue commandQueue) : IEventHandler<DownloadQueued>
{
    public async Task HandleAsync(DownloadQueued domainEvent, CancellationToken cancellationToken = default)
    {
        await commandQueue.EnqueueAsync(
            new AddDownloadCommand(
                domainEvent.AttemptId,
                domainEvent.IntentId,
                domainEvent.WorkId,
                domainEvent.TargetId,
                domainEvent.ReleaseGuid,
                domainEvent.DownloadUrl,
                domainEvent.UnitIds),
            idempotencyKey: $"add-download:{domainEvent.AttemptId}",
            cancellationToken);
    }
}
