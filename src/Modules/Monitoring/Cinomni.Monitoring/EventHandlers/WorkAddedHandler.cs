using Cinomni.Catalog.Contracts;
using Cinomni.Kernel.Messaging;
using Cinomni.Monitoring.Contracts;
using Cinomni.Monitoring.Messaging;
using Cinomni.Operations.Messaging;
using Microsoft.Extensions.Logging;

namespace Cinomni.Monitoring.EventHandlers;

/// <summary>
/// Reacts to a work being catalogued by applying its default monitoring policy — both movies and series
/// are monitored as soon as they are added. It <b>enqueues a command</b> instead of writing the target
/// directly: this handler runs inside the outbox relay's transaction, so the module write must happen in
/// its own unit of work. Idempotent by <c>apply-policy:{workId}</c> (the outbox is at-least-once),
/// which is safe because a work is added exactly once.
/// <para>
/// A series has no seasons at this point — <c>WorkAdded</c> fires before any metadata refresh — so this
/// only creates the policy root. The season and episode targets are materialised later, by
/// <see cref="SeriesStructureChangedHandler"/>, under a key that changes with every snapshot.
/// </para>
/// </summary>
public sealed class WorkAddedHandler(ICommandQueue commandQueue, ILogger<WorkAddedHandler> logger)
    : IEventHandler<WorkAdded>
{
    public async Task HandleAsync(WorkAdded domainEvent, CancellationToken cancellationToken = default)
    {
        var mode = DefaultModeFor(domainEvent.Kind);
        if (mode is not { } policy)
        {
            // Never silent: an unrecognised kind means a work nobody will ever search for.
            logger.LogWarning(
                "WorkAdded for work {WorkId} carried the unknown kind '{Kind}'; no monitoring policy applied.",
                domainEvent.WorkId,
                domainEvent.Kind);
            return;
        }

        // A work catalogued without wanting it looked for (a list's suggestion) still gets its root, so
        // the work page shows it and an operator can switch it on — it just starts switched off.
        // Initial only: a policy someone already set explicitly, or a request that already asked for the
        // title, must not be overwritten by this default arriving late.
        await commandQueue.EnqueueAsync(
            new ApplyMonitoringPolicyCommand(
                domainEvent.WorkId,
                domainEvent.Monitored ? policy : MonitoringMode.None,
                InitialOnly: true),
            idempotencyKey: $"apply-policy:{domainEvent.WorkId}",
            cancellationToken);
    }

    /// <summary>The policy a newly catalogued work starts with, or null when its kind is not recognised.</summary>
    private static MonitoringMode? DefaultModeFor(string kind)
    {
        if (!Enum.TryParse<WorkKind>(kind, ignoreCase: false, out var parsed) || !Enum.IsDefined(parsed))
        {
            return null;
        }

        return parsed switch
        {
            // A movie is one unit, so "all" is the single file. A series starts fully monitored and the
            // user narrows it afterwards — the opposite default would make an added show do nothing.
            WorkKind.Movie => MonitoringMode.All,
            WorkKind.Series => MonitoringMode.All,
            _ => null,
        };
    }
}
