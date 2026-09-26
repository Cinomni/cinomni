using Cinomni.Kernel.Messaging;
using Cinomni.Kernel.Results;
using Cinomni.Metadata.Application;
using Cinomni.Metadata.Persistence;
using Cinomni.Operations.Retention;
using Cinomni.Operations.Settings;
using Microsoft.Extensions.Logging;

namespace Cinomni.Metadata.Messaging;

/// <summary>
/// Removes a snapshot only when it is older than the window <b>and</b> no <c>refresh_states</c> row
/// points at it. That guard is what makes the sweep safe across the module boundary: Catalog copies
/// the snapshot id it was told about in <c>MetadataRefreshed</c>, which is published in the same unit
/// of work that sets <c>refresh_states.snapshot_id</c>, so the retained set is always a superset of
/// the set anything outside Metadata can be holding. Metadata cannot verify that itself — it may not
/// read the catalog schema — so the invariant is asserted by test instead.
/// <para>
/// Artwork, seasons and episodes follow through the schema's own cascades. Idempotent: a pure
/// predicate over time and reachability, so a redelivered command finds nothing left to remove.
/// </para>
/// </summary>
public sealed class PurgeSnapshotsCommandHandler(
    MetadataDbContext dbContext,
    ILiveOptions<MetadataOptions> options,
    RetentionOptions platformOptions,
    ILogger<PurgeSnapshotsCommandHandler> logger)
    : ICommandHandler<PurgeSnapshotsCommand>
{
    public async Task<Result> HandleAsync(
        PurgeSnapshotsCommand command,
        CancellationToken cancellationToken = default)
    {
        // Read at use time, never cached: a value the settings store just accepted must be honoured by
        // the very next run of this handler, not only after a restart.
        var cutoff = DateTimeOffset.UtcNow - options.Current.SnapshotRetention;

        var removed = await RetentionPurge.DeleteInBatchesAsync(
            dbContext.Snapshots,
            snapshot => snapshot.FetchedAt < cutoff
                && !dbContext.RefreshStates.Any(state => state.SnapshotId == snapshot.Id),
            snapshot => snapshot.Id,
            platformOptions.BatchSize,
            cancellationToken);

        logger.LogInformation(
            "Retention purge (metadata): removed {Removed} superseded snapshots with their artwork, "
            + "seasons and episodes.",
            removed);

        return Result.Success();
    }
}
