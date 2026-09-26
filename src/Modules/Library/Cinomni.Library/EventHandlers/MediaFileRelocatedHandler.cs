using Cinomni.Import.Contracts;
using Cinomni.Kernel.Messaging;
using Cinomni.Library.Messaging;
using Cinomni.Operations.Messaging;

namespace Cinomni.Library.EventHandlers;

/// <summary>
/// Reacts to Import's <see cref="MediaFileRelocated"/> by correcting the path the version stores.
/// Enqueues a command, as every consumer write does — the handler runs inside the outbox relay's
/// transaction, which the library context is not enlisted in. Idempotent by
/// <c>relocate-asset-file:{assetId}</c>.
/// <para>
/// Import moved the file first and this follows, never the reverse. Between the two the library names
/// a path that is no longer there, which is a window a repair pass closes by re-emitting; a row
/// updated before the move would instead name a path that does not exist <em>yet</em>, and nothing
/// would ever come back to fix it.
/// </para>
/// </summary>
public sealed class MediaFileRelocatedHandler(ICommandQueue commandQueue) : IEventHandler<MediaFileRelocated>
{
    public async Task HandleAsync(MediaFileRelocated domainEvent, CancellationToken cancellationToken = default) =>
        await commandQueue.EnqueueAsync(
            new RelocateAssetFileCommand(domainEvent.AssetId, domainEvent.FromPath, domainEvent.ToPath),
            idempotencyKey: $"relocate-asset-file:{domainEvent.AssetId}",
            cancellationToken);
}
