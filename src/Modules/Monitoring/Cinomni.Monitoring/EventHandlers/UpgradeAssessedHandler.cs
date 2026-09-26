using Cinomni.Decision.Contracts;
using Cinomni.Kernel.Messaging;
using Cinomni.Monitoring.Messaging;
using Cinomni.Operations.Messaging;

namespace Cinomni.Monitoring.EventHandlers;

/// <summary>
/// Reacts to Decision's <see cref="UpgradeAssessed"/> by recording, on the targets themselves, whether
/// what they hold is worth bettering. Enqueues a command: the handler runs in the relay's transaction
/// and cannot write the monitoring schema.
/// <para>
/// Monitoring stores the answer but never forms it. Whether a file is good enough is a question about
/// the acquisition profile, and the profile belongs to Decision.
/// </para>
/// </summary>
public sealed class UpgradeAssessedHandler(ICommandQueue commandQueue) : IEventHandler<UpgradeAssessed>
{
    public async Task HandleAsync(UpgradeAssessed domainEvent, CancellationToken cancellationToken = default)
    {
        if (domainEvent.UnitsWantingUpgrade.Count == 0 && domainEvent.UnitsSatisfied.Count == 0)
        {
            return;
        }

        await commandQueue.EnqueueAsync(
            new SetUpgradeWantedCommand(domainEvent.WorkId, domainEvent.UnitsWantingUpgrade, domainEvent.UnitsSatisfied),
            idempotencyKey: $"set-upgrade-wanted:{domainEvent.AssetId}",
            cancellationToken);
    }
}
