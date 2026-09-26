using System.Globalization;
using Cinomni.Kernel.Messaging;
using Cinomni.Kernel.Results;
using Cinomni.Metadata.Application;
using Cinomni.Metadata.Contracts;
using Cinomni.Metadata.Persistence;
using Cinomni.Operations.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Cinomni.Metadata.Messaging;

/// <summary>
/// Sweeps for series that are still producing episodes and whose short series TTL has lapsed, and
/// enqueues one <see cref="RefreshMetadataCommand"/> per (work, provider). It only <i>enqueues</i>: the
/// refresh service remains the single authority on whether a fetch actually runs, so a sweep that is
/// slightly eager costs a no-op command rather than a provider call.
/// <para>
/// The idempotency key carries an hour window because <c>operations.command</c>'s unique key is consumed
/// forever — a fixed key would let the very first sweep permanently suppress every later one.
/// </para>
/// </summary>
public sealed class RefreshContinuingSeriesCommandHandler(
    MetadataDbContext dbContext,
    ICommandQueue commandQueue,
    MetadataOptions options) : ICommandHandler<RefreshContinuingSeriesCommand>
{
    /// <summary>Keeps the composed idempotency key inside the 200-character column.</summary>
    private const int ProviderKeyMaxLength = 40;

    public async Task<Result> HandleAsync(
        RefreshContinuingSeriesCommand command,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var dueBefore = now - options.SeriesRefreshTtl;

        // The state points at the snapshot it last produced, so its status is the current one; a work
        // still refreshing or backing off is left to its own machinery.
        var due = await dbContext.RefreshStates
            .AsNoTracking()
            .Where(state => state.Status == MetadataRefreshStatus.Fresh
                && state.LastRefreshedAt != null
                && state.LastRefreshedAt <= dueBefore
                && dbContext.Snapshots.Any(snapshot => snapshot.Id == state.SnapshotId
                    && snapshot.Kind == MetadataMediaKind.Series
                    && (snapshot.SeriesStatus == SeriesStatus.Continuing || snapshot.SeriesStatus == SeriesStatus.Upcoming)))
            .OrderBy(state => state.LastRefreshedAt)
            .Take(Math.Max(1, options.MaxContinuingRefreshesPerSweep))
            .Select(state => new { state.WorkId, state.Provider, state.SnapshotId })
            .ToListAsync(cancellationToken);

        if (due.Count == 0)
        {
            return Result.Success();
        }

        // The provider's own id lives on the snapshot, not on the refresh state.
        var snapshotIds = due.Select(item => item.SnapshotId!.Value).ToList();
        var externalIds = await dbContext.Snapshots
            .AsNoTracking()
            .Where(snapshot => snapshotIds.Contains(snapshot.Id))
            .ToDictionaryAsync(snapshot => snapshot.Id, snapshot => snapshot.ExternalId, cancellationToken);

        var window = now.ToString("yyyyMMddHH", CultureInfo.InvariantCulture);
        foreach (var item in due)
        {
            if (!externalIds.TryGetValue(item.SnapshotId!.Value, out var externalId))
            {
                continue;
            }

            await commandQueue.EnqueueAsync(
                new RefreshMetadataCommand(item.WorkId, item.Provider, externalId, MetadataMediaKind.Series),
                idempotencyKey: $"metadata-refresh-continuing:{item.WorkId}:{Text.Truncate(item.Provider, ProviderKeyMaxLength)}:{window}",
                cancellationToken);
        }

        return Result.Success();
    }
}
