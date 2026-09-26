using Cinomni.Catalog.Contracts;
using Cinomni.Kernel.Messaging;
using Cinomni.Kernel.Results;
using Cinomni.Metadata.Contracts;

namespace Cinomni.Catalog.Messaging;

/// <summary>
/// Materialises a metadata snapshot's season/episode tree onto a series (⇠ Metadata's
/// <c>MetadataRefreshed</c>). Reads the tree through <see cref="IMetadataQuery"/> (Catalog →i Metadata,
/// the allowed direction) and translates it into Catalog's own vocabulary — the ACL, so
/// <c>Cinomni.Catalog.Contracts</c> stays Kernel-only.
/// <para>
/// A snapshot that no longer exists, a movie snapshot (whose tree comes back empty) and a snapshot whose
/// provider did not claim the numbering are all no-ops; the command succeeds so the queue does not retry.
/// </para>
/// </summary>
public sealed class SyncSeriesStructureCommandHandler(ICatalogCommands commands, IMetadataQuery metadata)
    : ICommandHandler<SyncSeriesStructureCommand>
{
    public async Task<Result> HandleAsync(
        SyncSeriesStructureCommand command,
        CancellationToken cancellationToken = default)
    {
        var snapshotId = new MetadataSnapshotId(command.SnapshotId);

        var structure = await metadata.GetSeriesStructureAsync(snapshotId, cancellationToken);
        if (structure is null || (structure.Seasons.Count == 0 && structure.Episodes.Count == 0))
        {
            return Result.Success(); // unknown snapshot, or a movie — nothing to materialise
        }

        // Only the snapshot knows which provider produced it, and that decides whether this tree may
        // renumber the work at all (Work.StructureProvider).
        var snapshot = await metadata.GetSnapshotAsync(snapshotId, cancellationToken);
        if (snapshot is null || snapshot.Kind != MetadataMediaKind.Series)
        {
            return Result.Success();
        }

        await commands.SyncSeriesStructureAsync(
            command.WorkId,
            new SeriesStructure(
                command.SnapshotId,
                snapshot.Provider,
                structure.Seasons.Select(ToSeasonInput).ToList(),
                structure.Episodes.Select(ToEpisodeInput).ToList()),
            cancellationToken);

        return Result.Success();
    }

    private static SeasonStructureInput ToSeasonInput(MetadataSeason season) => new(
        season.Number,
        season.Title,
        season.AirDate,
        season.EpisodeCount,
        season.PosterUrl);

    private static EpisodeStructureInput ToEpisodeInput(MetadataEpisode episode) => new(
        episode.SeasonNumber,
        episode.Number,
        episode.Title,
        episode.AbsoluteNumber,
        episode.AirDate,
        episode.AirDateTime,
        episode.RuntimeMinutes,
        episode.StillUrl);
}
