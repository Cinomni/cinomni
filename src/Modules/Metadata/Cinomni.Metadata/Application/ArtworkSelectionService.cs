using Cinomni.Kernel.Results;
using Cinomni.Metadata.Contracts;
using Cinomni.Metadata.Persistence;
using Cinomni.Operations.Messaging;
using Cinomni.Operations.Transactions;
using Microsoft.EntityFrameworkCore;

namespace Cinomni.Metadata.Application;

/// <summary>
/// Operator override of a snapshot's selected artwork (<see cref="IMetadataArtwork"/>): re-points the
/// selected poster/backdrop to another persisted candidate, then emits <c>MetadataArtworkSelected</c> so
/// Catalog updates the work's artwork. A no-op if the candidate is already selected. The write and its
/// event commit in one unit of work.
/// <para>
/// Selection is scoped by <c>(Kind, SeasonNumber, EpisodeNumber)</c>, not by kind alone. A season poster
/// and the series poster are both <see cref="ArtworkKind.Poster"/>, so clearing every row of the kind
/// would de-select the series poster the moment an operator picks a season image — and copying the pick
/// onto <c>snapshot.PosterUrl</c> would silently replace the series poster shown everywhere. Only a
/// series-level pick (both scope columns <c>null</c>) re-points the snapshot's own urls; for a movie
/// every row is series-level, so the behaviour is exactly what it always was.
/// </para>
/// </summary>
public sealed class ArtworkSelectionService(
    MetadataDbContext dbContext,
    IUnitOfWork unitOfWork,
    IEventBus eventBus) : IMetadataArtwork
{
    public async Task<Result> SelectAsync(MetadataSnapshotId snapshotId, Guid artworkId, CancellationToken cancellationToken = default)
    {
        var snapshot = await dbContext.Snapshots
            .Include(s => s.Artwork)
            .FirstOrDefaultAsync(s => s.Id == snapshotId.Value, cancellationToken);
        if (snapshot is null)
        {
            return Result.Failure(new Error("metadata.snapshot_not_found", "Snapshot not found."));
        }

        var target = snapshot.Artwork.FirstOrDefault(a => a.Id == artworkId);
        if (target is null)
        {
            return Result.Failure(new Error("metadata.artwork_not_found", "Artwork candidate not found for this snapshot."));
        }

        if (target.IsSelected)
        {
            return Result.Success(); // already the selected candidate for its scope — nothing to do
        }

        // Exactly one candidate is selected per (kind, season, episode): clear the others of this
        // scope only, so a season pick leaves the series selection — and every other season — alone.
        var scoped = snapshot.Artwork.Where(a =>
            a.Kind == target.Kind
            && a.SeasonNumber == target.SeasonNumber
            && a.EpisodeNumber == target.EpisodeNumber);
        foreach (var candidate in scoped)
        {
            candidate.IsSelected = candidate.Id == artworkId;
        }

        if (IsSeriesLevel(target))
        {
            if (target.Kind == ArtworkKind.Poster)
            {
                snapshot.PosterUrl = target.Url;
            }
            else if (target.Kind == ArtworkKind.Backdrop)
            {
                snapshot.BackdropUrl = target.Url;
            }
        }

        await unitOfWork.ExecuteAsync(async token =>
        {
            await dbContext.SaveChangesAsync(token);
            await eventBus.PublishAsync(
                new MetadataArtworkSelected(snapshot.WorkId, snapshot.Id, snapshot.PosterUrl, snapshot.BackdropUrl),
                token);
        }, cancellationToken);

        return Result.Success();
    }

    /// <summary>Whether a candidate belongs to the work as a whole rather than to one season or episode.</summary>
    private static bool IsSeriesLevel(MetadataArtworkRecord artwork) =>
        artwork.SeasonNumber is null && artwork.EpisodeNumber is null;
}
