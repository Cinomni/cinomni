using Cinomni.Catalog.Contracts;
using Cinomni.Catalog.Persistence;
using Cinomni.Kernel.Identifiers;
using Cinomni.Kernel.Results;
using Cinomni.Operations.Messaging;
using Cinomni.Operations.Transactions;
using Microsoft.EntityFrameworkCore;

namespace Cinomni.Catalog.Application;

public sealed class CatalogCommands(
    CatalogDbContext dbContext,
    IUnitOfWork unitOfWork,
    IEventBus eventBus,
    SeriesStructureService seriesStructure)
    : ICatalogCommands
{
    // Match the mapped Work column widths (CatalogDbContext) so an ACL copy never overflows the write.
    private const int TitleMaxLength = 500;
    private const int LanguageMaxLength = 20;

    /// <summary>Same cap as the snapshot's own overview column, so nothing the provider sent is cut twice.</summary>
    internal const int OverviewMaxLength = 8000;

    /// <summary>A work has a handful of genres; a provider answering with hundreds is not describing it.</summary>
    private const int MaxGenres = 32;

    public async Task<Result> RemoveWorkAsync(WorkId workId, bool deleteFiles, CancellationToken cancellationToken = default)
    {
        var work = await dbContext.Works
            .Include(w => w.ExternalIdentifiers)
            .FirstOrDefaultAsync(w => w.Id == workId.Value, cancellationToken);
        if (work is null)
        {
            return Result.Failure(new Error("catalog.work_not_found", "No work with this id is in the catalog."));
        }

        work.Remove(DateTimeOffset.UtcNow);

        // A list the work came from would otherwise add it straight back on its next run: the entry is
        // kept, marked, and the list refresh leaves a removed title alone.
        var entries = await dbContext.ImportListEntries
            .Where(e => e.WorkId == work.Id)
            .ToListAsync(cancellationToken);
        foreach (var entry in entries)
        {
            entry.Outcome = TrendingListRefresh.Removed;
        }

        await unitOfWork.ExecuteAsync(async token =>
        {
            await dbContext.SaveChangesAsync(token);
            await eventBus.PublishAsync(new WorkRemoved(work.Id, work.Kind.ToString(), work.Title, deleteFiles), token);
        }, cancellationToken);

        return Result.Success();
    }

    public Task<Result<WorkId>> AddMovieAsync(
        string title,
        int? year,
        IReadOnlyList<ExternalId> externalIds,
        CollectionId? collection = null,
        bool monitored = true,
        CancellationToken cancellationToken = default) =>
        AddWorkAsync(WorkKind.Movie, WorkStatus.Released, title, year, externalIds, collection, monitored, cancellationToken);

    public Task<Result<WorkId>> AddSeriesAsync(
        string title,
        int? year,
        IReadOnlyList<ExternalId> externalIds,
        CollectionId? collection = null,
        bool monitored = true,
        CancellationToken cancellationToken = default) =>
        // A newly added series is presumed to still be airing until a provider snapshot says otherwise;
        // AttachMetadataSnapshot narrows it to Ended (or Announced) from the snapshot's series status.
        AddWorkAsync(WorkKind.Series, WorkStatus.Continuing, title, year, externalIds, collection, monitored, cancellationToken);

    /// <summary>
    /// The one identity path both kinds of work take. Extracted so the rule-16 external-id dedupe exists
    /// exactly once, and so a series publishes the very same single <see cref="WorkAdded"/> a movie does —
    /// consumers need no new subscription and the "one outbox message per add" guarantee is unchanged.
    /// </summary>
    private async Task<Result<WorkId>> AddWorkAsync(
        WorkKind kind,
        WorkStatus initialStatus,
        string title,
        int? year,
        IReadOnlyList<ExternalId> externalIds,
        CollectionId? collection,
        bool monitored,
        CancellationToken cancellationToken)
    {
        var normalizedTitle = title.Trim();
        if (normalizedTitle.Length == 0)
        {
            return Result<WorkId>.Failure(new Error("catalog.invalid_title", "Title must not be empty."));
        }

        // Never mint a new identity for a work we already know by external id — of this kind:
        // a provider can number films and shows independently, so the same id can name one of each.
        foreach (var externalId in externalIds)
        {
            var existing = await dbContext.ExternalIdentifiers
                .Where(e => e.Provider == externalId.Provider && e.Value == externalId.Value && e.Kind == kind)
                .Select(e => (Guid?)e.WorkId)
                .FirstOrDefaultAsync(cancellationToken);

            if (existing is not null)
            {
                return Result<WorkId>.Success(new WorkId(existing.Value));
            }
        }

        // A work always sits somewhere: the named collection, or the one the installation defaults to.
        var collectionId = collection?.Value ?? await DefaultCollectionIdAsync(cancellationToken);

        var work = new Work
        {
            Id = Uuid7.New(),
            Kind = kind,
            CollectionId = collectionId,
            Title = normalizedTitle,
            SortTitle = SortTitles.Normalize(normalizedTitle),
            Year = year,
            Status = initialStatus,
            AddedAt = DateTimeOffset.UtcNow,
        };

        foreach (var externalId in externalIds)
        {
            work.ExternalIdentifiers.Add(new ExternalIdentifier
            {
                Id = Uuid7.New(),
                WorkId = work.Id,
                Provider = externalId.Provider,
                Value = externalId.Value,
                Kind = kind,
            });
        }

        // The work and its WorkAdded event commit together.
        await unitOfWork.ExecuteAsync(async token =>
        {
            dbContext.Works.Add(work);
            await dbContext.SaveChangesAsync(token);
            await eventBus.PublishAsync(new WorkAdded(work.Id, work.Kind.ToString(), work.Title, work.Year, monitored), token);
        }, cancellationToken);

        return Result<WorkId>.Success(new WorkId(work.Id));
    }

    /// <inheritdoc />
    public Task SyncSeriesStructureAsync(
        Guid workId,
        SeriesStructure structure,
        CancellationToken cancellationToken = default) =>
        // The upsert is a file of its own: it is the largest single behaviour in the module and the one
        // that must never delete a row (see SeriesStructureService).
        seriesStructure.SyncAsync(workId, structure, cancellationToken);

    /// <summary>
    /// The default collection's id. Seeded by the migration and by <c>MigrateCatalogAsync</c>, so it is
    /// always there; the constant is the fallback for a context that somehow has none.
    /// </summary>
    private async Task<Guid> DefaultCollectionIdAsync(CancellationToken cancellationToken)
    {
        var id = await dbContext.Collections
            .Where(c => c.IsDefault)
            .Select(c => (Guid?)c.Id)
            .FirstOrDefaultAsync(cancellationToken);

        return id ?? DefaultCollection.Id;
    }

    public async Task MarkWorkAvailableAsync(Guid workId, Guid targetId, CancellationToken cancellationToken = default)
    {
        var work = await dbContext.Works.FirstOrDefaultAsync(w => w.Id == workId, cancellationToken);

        // Unknown work (inter-schema ref, no FK) or already available — nothing to do. Setting the
        // flag is idempotent, and we only announce the first transition to available.
        if (work is null || work.HasAsset)
        {
            return;
        }

        work.HasAsset = true;
        await unitOfWork.ExecuteAsync(async token =>
        {
            await dbContext.SaveChangesAsync(token);
            await eventBus.PublishAsync(new WorkAvailable(workId, targetId), token);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public async Task MarkEpisodeAvailableAsync(
        Guid episodeId,
        Guid assetId,
        Guid targetId,
        CancellationToken cancellationToken = default)
    {
        var episode = await dbContext.Episodes.FirstOrDefaultAsync(e => e.Id == episodeId, cancellationToken);

        // Unknown episode (a stale unit id on a redelivered import) or already available. The second case
        // is the season-pack fan-out being redelivered at-least-once: every episode must count once.
        if (episode is null || !episode.MarkAvailable())
        {
            return;
        }

        var work = await dbContext.Works.FirstOrDefaultAsync(w => w.Id == episode.WorkId, cancellationToken);
        var wasAvailable = work is null || work.HasAsset;

        await unitOfWork.ExecuteAsync(async token =>
        {
            await dbContext.SaveChangesAsync(token);

            if (work is not null)
            {
                // Recount inside the transaction instead of incrementing: the rollup can then never drift
                // away from the rows, however many fan-out commands land for one import.
                work.AvailableEpisodeCount = await dbContext.Episodes
                    .CountAsync(e => e.WorkId == work.Id && e.HasAsset, token);
                work.HasAsset = work.AvailableEpisodeCount > 0;
                await dbContext.SaveChangesAsync(token);
            }

            await eventBus.PublishAsync(
                new EpisodeAvailable(
                    episode.WorkId,
                    episode.SeasonId,
                    episode.Id,
                    episode.SeasonNumber,
                    episode.Number,
                    assetId,
                    targetId),
                token);

            // Movie-era consumers (Monitoring, Notifications) only know the work-level transition, so the
            // work's FIRST available episode announces it exactly once — as a movie import would.
            if (!wasAvailable)
            {
                await eventBus.PublishAsync(new WorkAvailable(episode.WorkId, targetId), token);
            }
        }, cancellationToken);
    }

    public async Task AttachMetadataSnapshotAsync(
        Guid workId,
        Guid snapshotId,
        string title,
        string? originalLanguage,
        int? year,
        int? runtimeMinutes,
        string? posterUrl,
        string? backdropUrl,
        WorkStatus? status = null,
        IReadOnlyList<string>? genres = null,
        string? contentRating = null,
        string? overview = null,
        CancellationToken cancellationToken = default)
    {
        var work = await dbContext.Works.FirstOrDefaultAsync(w => w.Id == workId, cancellationToken);

        // Unknown work (inter-schema ref, no FK) or this snapshot already applied — nothing to do.
        if (work is null || work.MetadataSnapshotId == snapshotId)
        {
            return;
        }

        // Re-clamp at the ACL: a snapshot field can be wider than the Work column (title up to 1000,
        // language up to 200), so truncate to the column widths or a hostile/garbage value overflows the
        // write. Poster/backdrop share the snapshot's 1000-char cap, so they need no re-clamp.
        var normalizedTitle = Text.Truncate(title.Trim(), TitleMaxLength)!;
        if (normalizedTitle.Length == 0)
        {
            return; // never blank the title from a bad snapshot
        }

        work.ApplyMetadata(
            normalizedTitle, SortTitles.Normalize(normalizedTitle), Text.Truncate(originalLanguage, LanguageMaxLength),
            year, runtimeMinutes, posterUrl, backdropUrl, snapshotId, status,
            // Re-clamped and re-capped here like every other snapshot string: this is the boundary,
            // and a field that was bounded on the way into Metadata is still untrusted on the way out.
            [.. (genres ?? []).Select(genre => Text.Truncate(genre, LanguageMaxLength)).OfType<string>()
                .Where(genre => genre.Length > 0).Take(MaxGenres)],
            Text.Truncate(contentRating, LanguageMaxLength),
            Text.Truncate(overview?.Trim(), OverviewMaxLength));

        // Enrichment is a Catalog-internal write; no integration event (nothing downstream depends on it yet).
        await unitOfWork.ExecuteAsync(async token => await dbContext.SaveChangesAsync(token), cancellationToken);
    }

    public async Task UpdateWorkArtworkAsync(
        Guid workId,
        string? posterUrl,
        string? backdropUrl,
        CancellationToken cancellationToken = default)
    {
        var work = await dbContext.Works.FirstOrDefaultAsync(w => w.Id == workId, cancellationToken);
        if (work is null)
        {
            return; // unknown work (inter-schema ref, no FK) — nothing to do
        }

        work.UpdateArtwork(posterUrl, backdropUrl);

        // Artwork is a Catalog-internal write; no integration event (nothing downstream depends on it yet).
        await unitOfWork.ExecuteAsync(async token => await dbContext.SaveChangesAsync(token), cancellationToken);
    }
}
