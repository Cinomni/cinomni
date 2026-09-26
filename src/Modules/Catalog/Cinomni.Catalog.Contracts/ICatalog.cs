using Cinomni.Kernel.Results;

namespace Cinomni.Catalog.Contracts;

/// <summary>Public commands of the Catalog module.</summary>
public interface ICatalogCommands
{
    /// <summary>
    /// Adds a movie to the catalog. If any of the given external ids is already linked to a
    /// work, that work is returned instead of minting a new identity. The work lands in
    /// <paramref name="collection"/>, or in the default collection when none is named. With
    /// <paramref name="monitored"/> false the work is catalogued but nothing starts looking for it: its
    /// <c>WorkAdded</c> says so, and Monitoring leaves it unwatched until someone asks.
    /// </summary>
    Task<Result<WorkId>> AddMovieAsync(
        string title,
        int? year,
        IReadOnlyList<ExternalId> externalIds,
        CollectionId? collection = null,
        bool monitored = true,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a series to the catalog. Identical to <see cref="AddMovieAsync"/> in every respect
    /// except the work kind — including the rule-16 external-id dedupe and the single
    /// <c>WorkAdded</c> event it publishes — so consumers need no new subscription. The season and
    /// episode structure arrives later, through <see cref="SyncSeriesStructureAsync"/>.
    /// </summary>
    Task<Result<WorkId>> AddSeriesAsync(
        string title,
        int? year,
        IReadOnlyList<ExternalId> externalIds,
        CollectionId? collection = null,
        bool monitored = true,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a work from the catalog and publishes <c>WorkRemoved</c> in the same unit of work, which is
    /// what makes the other modules let go of it. Idempotent: removing a work that is already gone, or
    /// was never here, fails with <c>catalog.work_not_found</c> and publishes nothing.
    /// </summary>
    /// <param name="deleteFiles">Whether its downloaded and imported files should be deleted too.</param>
    Task<Result> RemoveWorkAsync(WorkId workId, bool deleteFiles, CancellationToken cancellationToken = default);

    /// <summary>
    /// Materialises a provider's season/episode tree onto a series (⇠ Metadata's <c>MetadataRefreshed</c>),
    /// emitting <c>SeriesStructureChanged</c>. Upserts by natural key — <c>(work, season number)</c> and
    /// <c>(work, season number, episode number)</c> — and never deletes: providers renumber specials, and a
    /// delete-then-insert would orphan every asset link, acquisition intent and monitored target pointing at
    /// the removed row. A structure with no seasons is a no-op, not a wipe. Idempotent: re-running the same
    /// snapshot changes nothing.
    /// </summary>
    Task SyncSeriesStructureAsync(
        Guid workId,
        SeriesStructure structure,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks a work as having a playable asset (⇠ Import's <c>MediaAvailable</c>), emitting
    /// <c>WorkAvailable</c>. No-op if the work is unknown or already available (idempotent).
    /// </summary>
    Task MarkWorkAvailableAsync(Guid workId, Guid targetId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks a single episode as having a playable asset (⇠ Import's <c>MediaAvailable</c>), emitting
    /// <c>EpisodeAvailable</c> and — only on the work's <em>first</em> available episode —
    /// <c>WorkAvailable</c>, so movie-era consumers observe the same work-level transition. No-op if the
    /// episode is unknown or already available, which is what makes a season-pack fan-out safe to redeliver.
    /// </summary>
    Task MarkEpisodeAvailableAsync(
        Guid episodeId,
        Guid assetId,
        Guid targetId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Enriches a work with a provider's metadata snapshot (⇠ Metadata's <c>MetadataRefreshed</c>): copies
    /// the neutral fields (including selected artwork) onto the work and records the snapshot id. The
    /// internal identity never changes. No-op if the work is unknown or the snapshot is
    /// already applied (idempotent).
    /// </summary>
    /// <param name="status">
    /// The production status the snapshot reports, mapped at the ACL, or <c>null</c> when the provider gave
    /// none — a status is written only when the snapshot actually supplies one. Trailing and optional: a
    /// caller that knows nothing about series lifecycles keeps the movie behaviour unchanged.
    /// </param>
    /// <param name="genres">The provider's genre names; empty leaves whatever the work already has.</param>
    /// <param name="contentRating">The classification for the configured region, or null to leave it alone.</param>
    /// <param name="overview">The provider's synopsis; null or blank leaves whatever the work already has.</param>
    Task AttachMetadataSnapshotAsync(
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
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-points a work's selected artwork (⇠ Metadata's <c>MetadataArtworkSelected</c>) without touching
    /// its descriptive fields. No-op if the work is unknown (idempotent).
    /// </summary>
    Task UpdateWorkArtworkAsync(
        Guid workId,
        string? posterUrl,
        string? backdropUrl,
        CancellationToken cancellationToken = default);
}

/// <summary>Public read model of the Catalog module.</summary>
public interface ICatalogQuery
{
    Task<WorkSummary?> GetByIdAsync(WorkId id, CancellationToken cancellationToken = default);

    /// <summary>
    /// The work that carries this external id. A provider can number films and shows independently
    /// (TMDB does), so one id can belong to a film and to a show: pass <paramref name="kind"/> whenever it
    /// is known. Without it the first match answers, which is only right for an id no other kind shares.
    /// </summary>
    Task<WorkSummary?> FindByExternalIdAsync(
        MetadataProvider provider,
        string value,
        WorkKind? kind = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkSummary>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Several works in one read — for a caller holding a page of ids, where one
    /// <see cref="GetByIdAsync"/> per id is an N+1. Unknown ids are simply absent. The default answers
    /// one by one; Catalog's own implementation answers in a single query.
    /// </summary>
    async Task<IReadOnlyList<WorkSummary>> GetByIdsAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken = default)
    {
        var works = new List<WorkSummary>(ids.Count);
        foreach (var id in ids)
        {
            if (await GetByIdAsync(new WorkId(id), cancellationToken) is { } work)
            {
                works.Add(work);
            }
        }

        return works;
    }
}

/// <summary>
/// Resolves release/file numbering to the concrete catalog units it covers, and reads a series tree.
/// Deliberately a <em>separate</em> interface from <see cref="ICatalogQuery"/>, whose surface Monitoring
/// and Notifications already consume and which must stay untouched.
/// <para>
/// Every resolver returns a LIST. That is what makes the N:M asset↔unit relation expressible: a season
/// pack covers many episodes, a multi-episode file covers two, and two episodes can share an air date.
/// An unknown work, season or episode yields an empty list — never an exception.
/// </para>
/// </summary>
public interface ICatalogSeriesQuery
{
    /// <summary>Resolves a single <c>SxxEyy</c> to its episode (0 or 1 result).</summary>
    Task<IReadOnlyList<EpisodeSummary>> ResolveEpisodeAsync(
        WorkId workId,
        int seasonNumber,
        int episodeNumber,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves an inclusive multi-episode range (<c>S01E01-E03</c>) to the episodes that exist inside it,
    /// ordered by episode number. Missing members of the range are simply absent from the result.
    /// </summary>
    Task<IReadOnlyList<EpisodeSummary>> ResolveEpisodeRangeAsync(
        WorkId workId,
        int seasonNumber,
        int firstEpisodeNumber,
        int lastEpisodeNumber,
        CancellationToken cancellationToken = default);

    /// <summary>Resolves a season pack to every episode of that season, ordered by episode number.</summary>
    Task<IReadOnlyList<EpisodeSummary>> ResolveSeasonAsync(
        WorkId workId,
        int seasonNumber,
        CancellationToken cancellationToken = default);

    /// <summary>Resolves an anime-style absolute number to its episode (0 or 1 result).</summary>
    Task<IReadOnlyList<EpisodeSummary>> ResolveByAbsoluteNumberAsync(
        WorkId workId,
        int absoluteNumber,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a date-based release (<c>Show.2026.07.28</c>) to the episodes that aired that day —
    /// a list, because a daily show can air two episodes on one date.
    /// </summary>
    Task<IReadOnlyList<EpisodeSummary>> ResolveByAirDateAsync(
        WorkId workId,
        DateOnly airDate,
        CancellationToken cancellationToken = default);

    /// <summary>Every season of a work, ordered by season number (specials, season 0, come first).</summary>
    Task<IReadOnlyList<SeasonSummary>> GetSeasonsAsync(
        WorkId workId,
        CancellationToken cancellationToken = default);

    /// <summary>Every episode of one season, ordered by episode number.</summary>
    Task<IReadOnlyList<EpisodeSummary>> GetEpisodesAsync(
        WorkId workId,
        int seasonNumber,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Every episode of a work, ordered by season then episode number, in <b>one</b> round trip.
    /// "Next up" and any whole-series view need the complete ordering; walking the seasons and asking
    /// for each one's episodes would be an N+1 on a page that renders often.
    /// </summary>
    Task<IReadOnlyList<EpisodeSummary>> GetAllEpisodesAsync(
        WorkId workId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// One episode of a work by its catalog unit id, or <c>null</c>. The acquisition spine correlates on
    /// unit ids, so a consumer holding an asset's <c>UnitIds</c> (Subtitles, Playback) arrives with an id
    /// and no numbering, and needs exactly this to recover <c>SxxEyy</c>.
    /// </summary>
    Task<EpisodeSummary?> GetEpisodeAsync(
        WorkId workId,
        EpisodeId episodeId,
        CancellationToken cancellationToken = default);
}
