using Cinomni.Kernel.Messaging;
using Cinomni.Library.Messaging;
using Cinomni.Operations.Messaging;
using Cinomni.Subtitles.Contracts;

namespace Cinomni.Library.EventHandlers;

/// <summary>
/// Reacts to Subtitles' <see cref="SubtitleAvailable"/> by enriching the asset with an external
/// subtitle track. Enqueues a command (the handler runs in the outbox relay's transaction, so the
/// library write happens in its own unit of work); idempotent by
/// <c>add-external-subtitle:{subtitleId}</c>. Subtitles owns the file and its metadata; Library only
/// records that the track exists on the asset.
/// </summary>
public sealed class SubtitleAvailableHandler(ICommandQueue commandQueue) : IEventHandler<SubtitleAvailable>
{
    public async Task HandleAsync(SubtitleAvailable domainEvent, CancellationToken cancellationToken = default) =>
        await commandQueue.EnqueueAsync(
            new AddExternalSubtitleCommand(domainEvent.AssetId, domainEvent.Language, domainEvent.Forced),
            idempotencyKey: $"add-external-subtitle:{domainEvent.SubtitleId}",
            cancellationToken);
}
