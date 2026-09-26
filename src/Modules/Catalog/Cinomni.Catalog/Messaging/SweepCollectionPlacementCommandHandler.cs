using Cinomni.Catalog.Application;
using Cinomni.Kernel.Messaging;
using Cinomni.Kernel.Results;

namespace Cinomni.Catalog.Messaging;

/// <summary>Runs one batch of a collection placement sweep and queues the next (see <see cref="CollectionPlacementService"/>).</summary>
public sealed class SweepCollectionPlacementCommandHandler(CollectionPlacementService placement)
    : ICommandHandler<SweepCollectionPlacementCommand>
{
    public async Task<Result> HandleAsync(SweepCollectionPlacementCommand command, CancellationToken cancellationToken = default)
    {
        await placement.SweepBatchAsync(command.RunId, command.After, queueNext: true, cancellationToken);
        return Result.Success();
    }
}
