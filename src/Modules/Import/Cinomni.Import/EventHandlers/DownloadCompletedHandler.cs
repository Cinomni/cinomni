using Cinomni.Downloads.Contracts;
using Cinomni.Import.Messaging;
using Cinomni.Kernel.Messaging;
using Cinomni.Operations.Messaging;

namespace Cinomni.Import.EventHandlers;

/// <summary>
/// Reacts to Downloads' <see cref="DownloadCompleted"/> by landing the content into the library.
/// Enqueues a command (the file operations and ffprobe are out-of-process side effects that must be
/// retryable); idempotent by <c>process-completed-download:{downloadTaskId}</c>. Downloads must not
/// know about the Work or the import — Import reacts to its event.
/// </summary>
public sealed class DownloadCompletedHandler(ICommandQueue commandQueue) : IEventHandler<DownloadCompleted>
{
    public async Task HandleAsync(DownloadCompleted domainEvent, CancellationToken cancellationToken = default) =>
        await commandQueue.EnqueueAsync(
            new ProcessCompletedDownloadCommand(
                domainEvent.DownloadTaskId,
                domainEvent.IntentId,
                domainEvent.AttemptId,
                domainEvent.WorkId,
                domainEvent.TargetId,
                domainEvent.ContentPath,
                domainEvent.UnitIds),
            idempotencyKey: $"process-completed-download:{domainEvent.DownloadTaskId}",
            cancellationToken);
}
