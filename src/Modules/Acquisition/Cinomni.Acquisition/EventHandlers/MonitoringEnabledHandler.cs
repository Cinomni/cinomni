using Cinomni.Acquisition.Messaging;
using Cinomni.Kernel.Messaging;
using Cinomni.Monitoring.Contracts;
using Cinomni.Operations.Messaging;
using Microsoft.Extensions.Logging;

namespace Cinomni.Acquisition.EventHandlers;

/// <summary>
/// Reacts to Monitoring's <see cref="MonitoringEnabled"/> by opening the persistent acquisition goal
/// for the target. It <b>enqueues a command</b> rather than writing directly: this handler runs
/// inside the outbox relay's transaction, so the module write must happen in its own unit of work.
/// Idempotent by <c>create-intent:{targetId}</c> — one goal per target (the outbox is
/// at-least-once).
/// <para>
/// The <c>Series</c> root is skipped. It is a policy node, not something acquirable: seasons and
/// episodes are what get downloaded, and opening a goal for the root would mean a 200-episode show
/// carries a goal that no release can ever satisfy.
/// </para>
/// </summary>
public sealed class MonitoringEnabledHandler(
    ICommandQueue commandQueue,
    ILogger<MonitoringEnabledHandler> logger) : IEventHandler<MonitoringEnabled>
{
    /// <summary>The one target kind that is a policy root rather than an acquisition goal.</summary>
    private const string SeriesKind = "Series";

    public async Task HandleAsync(MonitoringEnabled domainEvent, CancellationToken cancellationToken = default)
    {
        // Kind is a trailing optional: a pre-series row deserializes with it null, which means the
        // event predates the hierarchy and is therefore a movie — acquirable.
        if (string.Equals(domainEvent.Kind, SeriesKind, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogDebug(
                "Target {TargetId} is the series root for work {WorkId}; it holds policy, not a goal.",
                domainEvent.TargetId, domainEvent.WorkId);
            return;
        }

        await commandQueue.EnqueueAsync(
            new CreateAcquisitionIntentCommand(
                domainEvent.TargetId, domainEvent.WorkId, domainEvent.Mode, domainEvent.UnitId),
            idempotencyKey: $"create-intent:{domainEvent.TargetId}",
            cancellationToken);
    }
}
