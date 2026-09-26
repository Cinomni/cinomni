using Cinomni.Acquisition.Messaging;
using Cinomni.Import.Contracts;
using Cinomni.Kernel.Messaging;
using Cinomni.Operations.Messaging;

namespace Cinomni.Acquisition.EventHandlers;

/// <summary>
/// Closes the acquisition spine at the import stage: Import reports back on the attempt, and the goal
/// advances. Each handler enqueues a command (it runs inside the outbox relay's transaction, so the
/// module write happens in its own unit of work). Idempotent by the import job id.
/// </summary>
public sealed class ImportCompletedHandler(ICommandQueue commandQueue) : IEventHandler<ImportCompleted>
{
    public async Task HandleAsync(ImportCompleted domainEvent, CancellationToken cancellationToken = default)
    {
        await commandQueue.EnqueueAsync(
            new MarkImportedCommand(domainEvent.IntentId),
            idempotencyKey: $"mark-imported:{domainEvent.ImportJobId}",
            cancellationToken);

        // The acquiring goal is met above. Everything else the same content satisfied — the nine
        // episode goals a season pack closed, none of which ever opened an attempt — is met here.
        if (domainEvent.UnitIds is { Count: > 0 } unitIds)
        {
            await commandQueue.EnqueueAsync(
                new MarkUnitsSatisfiedCommand(unitIds, domainEvent.AssetId),
                idempotencyKey: $"units-satisfied:{domainEvent.ImportJobId}",
                cancellationToken);
        }
    }
}

/// <summary>Reacts to a failed import by returning the goal to searching (or exhausting it).</summary>
public sealed class ImportFailedHandler(ICommandQueue commandQueue) : IEventHandler<ImportFailed>
{
    public async Task HandleAsync(ImportFailed domainEvent, CancellationToken cancellationToken = default) =>
        await commandQueue.EnqueueAsync(
            new MarkImportFailedCommand(domainEvent.IntentId, domainEvent.Reason),
            idempotencyKey: $"mark-import-failed:{domainEvent.ImportJobId}",
            cancellationToken);
}
