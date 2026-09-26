using Cinomni.Acquisition.Contracts;
using Cinomni.Acquisition.Messaging;
using Cinomni.Decision.Contracts;
using Cinomni.Kernel.Messaging;
using Cinomni.Operations.Messaging;
using Microsoft.Extensions.Logging;

namespace Cinomni.Acquisition.EventHandlers;

/// <summary>
/// Reacts to Decision's <see cref="ReleaseSelected"/> by feeding the chosen candidate into the
/// target's goal. Enqueues a command (same reason as <see cref="MonitoringEnabledHandler"/>);
/// idempotent by <c>select-candidate:{evaluationId}:{targetId}</c>.
/// <para>
/// The key carries the target as well as the evaluation because one evaluation is no longer
/// guaranteed to route to one goal: with a hierarchy, the same decision can legitimately be offered
/// to more than one target, and the bare <c>select-candidate:{evaluationId}</c> key would silently
/// deduplicate all but the first away — the idempotency table has no cleanup, so that loss is
/// permanent.
/// </para>
/// </summary>
public sealed class ReleaseSelectedHandler(
    ICommandQueue commandQueue,
    ILogger<ReleaseSelectedHandler> logger) : IEventHandler<ReleaseSelected>
{
    public async Task HandleAsync(ReleaseSelected domainEvent, CancellationToken cancellationToken = default)
    {
        if (domainEvent.TargetId is not { } targetId)
        {
            // Without a target there is nothing to key on and nothing to route to. The command
            // handler can fall back to the units, but only once it knows which goal to look at.
            logger.LogWarning(
                "Dropping selection {EvaluationId} ({ReleaseGuid}): it carries no target.",
                domainEvent.EvaluationId, domainEvent.ReleaseGuid);
            return;
        }

        await commandQueue.EnqueueAsync(
            new SelectCandidateCommand(
                domainEvent.EvaluationId,
                targetId,
                domainEvent.ReleaseGuid,
                domainEvent.DownloadUrl,
                domainEvent.UnitIds,
                domainEvent.Release is { } release
                    ? new AttemptRelease(release.Title, release.IndexerName, release.Seeders, release.Leechers)
                    : null),
            idempotencyKey: $"select-candidate:{domainEvent.EvaluationId}:{targetId}",
            cancellationToken);
    }
}
