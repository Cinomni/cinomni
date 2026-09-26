using Cinomni.Acquisition.Messaging;
using Cinomni.Downloads.Contracts;
using Cinomni.Kernel.Messaging;
using Cinomni.Operations.Messaging;

namespace Cinomni.Acquisition.EventHandlers;

/// <summary>
/// Closes the acquisition spine: Downloads reports back on the attempt, and the goal advances. Each
/// handler enqueues a command (it runs inside the outbox relay's transaction, so the module write
/// happens in its own unit of work).
/// <para>
/// Idempotent by download task <b>and goal</b>. Two goals can share one download task when they
/// selected the same release — Downloads then reports to each of them — and a key naming only the
/// task would deduplicate the second goal's feedback away and leave it stuck in <c>Downloading</c>
/// with nothing left to advance it. The commands are all state-guarded no-ops, so the finer key
/// costs nothing on the ordinary one-goal path.
/// </para>
/// </summary>
public sealed class DownloadStartedHandler(ICommandQueue commandQueue) : IEventHandler<DownloadStarted>
{
    public async Task HandleAsync(DownloadStarted domainEvent, CancellationToken cancellationToken = default) =>
        await commandQueue.EnqueueAsync(
            new MarkDownloadStartedCommand(domainEvent.IntentId),
            idempotencyKey: $"mark-download-started:{domainEvent.DownloadTaskId}:{domainEvent.IntentId}",
            cancellationToken);
}

/// <summary>Reacts to a completed download by advancing the goal to Importing.</summary>
public sealed class DownloadCompletedHandler(ICommandQueue commandQueue) : IEventHandler<DownloadCompleted>
{
    public async Task HandleAsync(DownloadCompleted domainEvent, CancellationToken cancellationToken = default) =>
        await commandQueue.EnqueueAsync(
            new MarkDownloadCompletedCommand(domainEvent.IntentId),
            idempotencyKey: $"mark-download-completed:{domainEvent.DownloadTaskId}:{domainEvent.IntentId}",
            cancellationToken);
}

/// <summary>
/// Reacts to a release that never became a download the same way: the goal returns to searching (or
/// exhausts). Keyed by attempt, because there is no task to key it by.
/// </summary>
public sealed class DownloadNotStartedHandler(ICommandQueue commandQueue) : IEventHandler<DownloadNotStarted>
{
    public async Task HandleAsync(DownloadNotStarted domainEvent, CancellationToken cancellationToken = default) =>
        await commandQueue.EnqueueAsync(
            new MarkDownloadNotStartedCommand(domainEvent.IntentId, domainEvent.AttemptId, domainEvent.Reason),
            idempotencyKey: $"mark-download-not-started:{domainEvent.AttemptId}",
            cancellationToken);
}

/// <summary>Reacts to a failed download by returning the goal to searching (or exhausting it).</summary>
public sealed class DownloadFailedHandler(ICommandQueue commandQueue) : IEventHandler<DownloadFailed>
{
    public async Task HandleAsync(DownloadFailed domainEvent, CancellationToken cancellationToken = default) =>
        await commandQueue.EnqueueAsync(
            new MarkDownloadFailedCommand(domainEvent.IntentId, domainEvent.Reason),
            idempotencyKey: $"mark-download-failed:{domainEvent.DownloadTaskId}:{domainEvent.IntentId}",
            cancellationToken);
}
