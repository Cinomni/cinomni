using Cinomni.Decision.Messaging;
using Cinomni.Kernel.Messaging;
using Cinomni.Library.Contracts;
using Cinomni.Operations.Messaging;

namespace Cinomni.Decision.EventHandlers;

/// <summary>
/// Reacts to Library's <see cref="MediaAssetRegistered"/> by asking whether what just landed is good
/// enough. Enqueues a command, as an event handler must: this one runs inside the relay's transaction
/// and the assessment reads the library and writes the outbox in its own unit of work.
/// <para>
/// It listens for the registration rather than Import's <c>MediaAvailable</c>, and the difference
/// matters: the assessment reads the quality Library persists, and only this event is published after
/// that row exists. Reacting to the earlier one would race the write it depends on.
/// </para>
/// </summary>
public sealed class MediaAssetRegisteredHandler(ICommandQueue commandQueue) : IEventHandler<MediaAssetRegistered>
{
    public async Task HandleAsync(MediaAssetRegistered domainEvent, CancellationToken cancellationToken = default)
    {
        var unitIds = domainEvent.UnitIds ?? [];
        if (unitIds.Count == 0)
        {
            // Nothing to judge per unit. An in-flight row from before unit links existed, and the sweep
            // simply leaves that target alone rather than guessing.
            return;
        }

        await commandQueue.EnqueueAsync(
            new AssessUpgradeCommand(domainEvent.AssetId, domainEvent.WorkId, unitIds),
            idempotencyKey: $"assess-upgrade:{domainEvent.AssetId}",
            cancellationToken);
    }
}
