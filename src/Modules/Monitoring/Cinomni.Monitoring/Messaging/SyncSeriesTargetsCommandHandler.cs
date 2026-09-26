using Cinomni.Kernel.Messaging;
using Cinomni.Kernel.Results;
using Cinomni.Monitoring.Application;

namespace Cinomni.Monitoring.Messaging;

/// <summary>
/// Runs <see cref="SyncSeriesTargetsCommand"/> through the materialiser: create the targets the catalog
/// structure now has, cascade the root's mode onto the new ones, and roll the missing state back up.
/// <para>
/// Deliberately does <em>not</em> re-cascade over existing targets. The same series is synced again on
/// every snapshot, and a re-cascade would silently undo a per-episode toggle the user made between two
/// metadata refreshes.
/// </para>
/// </summary>
public sealed class SyncSeriesTargetsCommandHandler(SeriesTargetMaterializer materializer)
    : ICommandHandler<SyncSeriesTargetsCommand>
{
    public async Task<Result> HandleAsync(
        SyncSeriesTargetsCommand command,
        CancellationToken cancellationToken = default)
    {
        await materializer.MaterializeAsync(command.WorkId, reapplyCascade: false, cancellationToken);
        return Result.Success();
    }
}
