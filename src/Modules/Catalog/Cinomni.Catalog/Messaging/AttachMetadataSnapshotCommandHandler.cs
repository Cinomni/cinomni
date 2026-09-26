using Cinomni.Catalog.Contracts;
using Cinomni.Kernel.Messaging;
using Cinomni.Kernel.Results;
using Cinomni.Metadata.Contracts;

namespace Cinomni.Catalog.Messaging;

/// <summary>
/// Enriches a work with a metadata snapshot (⇠ Metadata's <c>MetadataRefreshed</c>). Reads the neutral
/// snapshot from Metadata by interface (Catalog →i Metadata, allowed) and copies its fields onto the
/// work. Idempotent — a re-applied snapshot is a no-op.
/// </summary>
public sealed class AttachMetadataSnapshotCommandHandler(ICatalogCommands commands, IMetadataQuery metadata)
    : ICommandHandler<AttachMetadataSnapshotCommand>
{
    public async Task<Result> HandleAsync(AttachMetadataSnapshotCommand command, CancellationToken cancellationToken = default)
    {
        var snapshot = await metadata.GetSnapshotAsync(new MetadataSnapshotId(command.SnapshotId), cancellationToken);
        if (snapshot is null)
        {
            return Result.Success(); // snapshot no longer exists; nothing to attach
        }

        await commands.AttachMetadataSnapshotAsync(
            command.WorkId,
            command.SnapshotId,
            snapshot.Title,
            snapshot.OriginalLanguage,
            snapshot.Year,
            snapshot.RuntimeMinutes,
            snapshot.PosterUrl,
            snapshot.BackdropUrl,
            ToWorkStatus(snapshot.SeriesStatus),
            snapshot.Genres,
            snapshot.ContentRating,
            snapshot.Overview,
            cancellationToken);

        return Result.Success();
    }

    /// <summary>
    /// Translates the provider-neutral series status into the catalog's own lifecycle — this is the ACL,
    /// so Metadata's enum never reaches Catalog's tables. A movie snapshot carries no series status, and
    /// <see cref="SeriesStatus.Unknown"/> (the provider gave nothing we map) yields <c>null</c>, which
    /// leaves the stored status untouched.
    /// </summary>
    private static WorkStatus? ToWorkStatus(SeriesStatus? status) => status switch
    {
        SeriesStatus.Continuing => WorkStatus.Continuing,

        // A cancelled show is as final as an ended one: its episode list will not grow again.
        SeriesStatus.Ended or SeriesStatus.Cancelled => WorkStatus.Ended,
        SeriesStatus.Upcoming => WorkStatus.Announced,
        _ => null,
    };
}
