using Cinomni.Import.Contracts;
using Cinomni.Kernel.Messaging;
using Cinomni.Operations.Messaging;
using Cinomni.Subtitles.Messaging;

namespace Cinomni.Subtitles.EventHandlers;

/// <summary>
/// Reacts to Import's <see cref="MediaFileRelocated"/> by catching this module's sidecar paths up with
/// the video they belong to. Enqueues a command, as every consumer write does. Idempotent by
/// <c>relocate-subtitles:{assetId}</c>.
/// <para>
/// Subtitles does not move the files: they sit in the video's directory and are named from its stem,
/// so Import moves them along with it and this module owns only the rows that point at them. Leaving
/// them behind would strand a subtitle the household asked for, next to a video that is no longer
/// there, under a name nothing would ever look up again.
/// </para>
/// </summary>
public sealed class MediaFileRelocatedHandler(ICommandQueue commandQueue) : IEventHandler<MediaFileRelocated>
{
    public async Task HandleAsync(MediaFileRelocated domainEvent, CancellationToken cancellationToken = default) =>
        await commandQueue.EnqueueAsync(
            new RelocateSubtitlesCommand(domainEvent.AssetId, domainEvent.FromPath, domainEvent.ToPath),
            idempotencyKey: $"relocate-subtitles:{domainEvent.AssetId}",
            cancellationToken);
}
