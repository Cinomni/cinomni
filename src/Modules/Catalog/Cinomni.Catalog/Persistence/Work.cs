using Cinomni.Catalog.Contracts;

namespace Cinomni.Catalog.Persistence;

/// <summary>
/// The Catalog aggregate root. Its identity (<see cref="Id"/>) is internal and stable —
/// it never changes when an external id or file path changes, and a work with no
/// external provider at all is valid.
/// </summary>
public sealed class Work
{
    /// <summary>Maximum persisted length of <see cref="StructureProvider"/>.</summary>
    public const int StructureProviderMaxLength = 50;

    public Guid Id { get; init; }

    public WorkKind Kind { get; init; }

    /// <summary>
    /// The collection this work sits in — the unit access is granted on. Never null, and it defaults to
    /// the shelf every installation has, so a caller that never thought about collections still writes a
    /// work that satisfies the foreign key. That is the same rule the migration applied to the works that
    /// already existed: unplaced means default shelf, not unplaced.
    /// </summary>
    public Guid CollectionId { get; set; } = DefaultCollection.Id;

    /// <summary>
    /// Whether an administrator put this work where it is by hand. A pinned work is out of the rules'
    /// reach until it is unpinned: an override the next sweep undoes is not an override.
    /// </summary>
    public bool CollectionPinned { get; private set; }

    /// <summary>
    /// The rule that placed this work, or null when no rule claims it (it sits on the default collection)
    /// or it is pinned. Persisted rather than logged because it is the answer to "why can this account
    /// see this title".
    /// </summary>
    public Guid? PlacedByRuleId { get; private set; }

    /// <summary>Puts the work on a collection by hand and pins it there.</summary>
    public void PinTo(Guid collectionId)
    {
        CollectionId = collectionId;
        CollectionPinned = true;
        PlacedByRuleId = null;
    }

    /// <summary>
    /// Places the work where the rules decided. A pinned work is left alone. Returns whether it changed
    /// collection — the event that changes who may see it; a new rule id on the same shelf is not one.
    /// </summary>
    public bool PlaceByRule(Guid collectionId, Guid? ruleId)
    {
        if (CollectionPinned)
        {
            return false;
        }

        var moved = CollectionId != collectionId;
        CollectionId = collectionId;
        PlacedByRuleId = ruleId;
        return moved;
    }

    public required string Title { get; set; }

    /// <summary>Title normalized for ordering (leading articles dropped, lower-cased).</summary>
    public required string SortTitle { get; set; }

    public string? OriginalLanguage { get; set; }

    /// <summary>
    /// The genre names the metadata provider published, in its order. Stored on the work rather than
    /// in a child table because a genre here is a label a provider used, not an entity this system
    /// owns — nothing points at one, and a table would invite reconciling two providers' vocabularies
    /// as though that had a single right answer.
    /// </summary>
    public List<string> Genres { get; set; } = [];

    /// <summary>
    /// The age classification for the region this installation is configured for, or null. Null is
    /// ordinary — an unconfigured region, a provider with nothing for it, or a genuinely unrated work
    /// all read the same — and anything deciding what to hide has to treat "unrated" as watchable, or
    /// it hides most of a library the day it is switched on.
    /// </summary>
    public string? ContentRating { get; set; }

    public int? Year { get; set; }

    public WorkStatus Status { get; set; }

    public int? RuntimeMinutes { get; set; }

    /// <summary>
    /// The synopsis the metadata provider published (⇠ Metadata), if any. Copied onto the work because the
    /// snapshot it comes from is administrator-only, and a household member reading a title's page needs
    /// to be told what it is about.
    /// </summary>
    public string? Overview { get; set; }

    /// <summary>Availability: whether the work has a playable asset (set later by MarkWorkAvailable).</summary>
    public bool HasAsset { get; set; }

    /// <summary>The metadata snapshot currently applied to this work (⇠ Metadata's MetadataRefreshed), if any.</summary>
    public Guid? MetadataSnapshotId { get; private set; }

    /// <summary>Selected poster artwork (⇠ Metadata), if any.</summary>
    public string? PosterUrl { get; private set; }

    /// <summary>Selected backdrop artwork (⇠ Metadata), if any.</summary>
    public string? BackdropUrl { get; private set; }

    /// <summary>
    /// The metadata provider that <em>claimed</em> this series' season/episode numbering. The first provider
    /// whose structure lands sets it; a later snapshot from the same provider re-syncs the structure, and a
    /// snapshot from a different provider only enriches descriptively and leaves the numbering alone.
    /// <para>
    /// Without this, two providers with different orderings (TheTVDB official vs. DVD vs. TVMaze) would fight
    /// over the same <c>SxxEyy</c> slots and quietly re-point episodes that releases, monitored targets and
    /// asset links already refer to. Null for movies and for a series whose structure has never synced.
    /// </para>
    /// </summary>
    public string? StructureProvider { get; set; }

    /// <summary>
    /// Rollup: how many episodes this work has. Denormalised so the library grid stays a single flat query —
    /// an <c>Include</c> of seasons and episodes there is an immediate cartesian blow-up. 0 for a movie.
    /// </summary>
    public int EpisodeCount { get; set; }

    /// <summary>Rollup: how many of those episodes have a playable asset. 0 for a movie.</summary>
    public int AvailableEpisodeCount { get; set; }

    public DateTimeOffset AddedAt { get; init; }

    /// <summary>
    /// When an administrator removed the work, or null while it is in the catalog. A removed work is kept
    /// (soft delete) so other modules can still correlate what they held for it, but no catalog read sees it.
    /// </summary>
    public DateTimeOffset? RemovedAt { get; private set; }

    public bool IsRemoved => RemovedAt is not null;

    /// <summary>
    /// Takes the work out of the catalog. Its external ids go with it: they are what makes a title "already
    /// known", and a removed title must be addable again as a new work rather than resolving to this one.
    /// </summary>
    public void Remove(DateTimeOffset now)
    {
        RemovedAt ??= now;
        ExternalIdentifiers.Clear();
    }

    public List<ExternalIdentifier> ExternalIdentifiers { get; } = [];

    /// <summary>The seasons of a series (always empty for a movie); cascade children.</summary>
    public List<Season> Seasons { get; } = [];

    /// <summary>
    /// Copies a provider's neutral metadata snapshot onto this work (the ACL). The internal
    /// identity never changes; only descriptive fields are enriched, and a field is overwritten only when
    /// the snapshot provides a value.
    /// </summary>
    /// <param name="status">
    /// The production status the snapshot reports, already mapped to a <see cref="WorkStatus"/>, or
    /// <c>null</c> when the provider gave none. Applied to a <see cref="WorkKind.Series"/> only: a movie's
    /// <see cref="WorkStatus.Released"/> is not a series lifecycle and must never be narrowed here.
    /// </param>
    public void ApplyMetadata(
        string title,
        string sortTitle,
        string? originalLanguage,
        int? year,
        int? runtimeMinutes,
        string? posterUrl,
        string? backdropUrl,
        Guid snapshotId,
        WorkStatus? status = null,
        IReadOnlyList<string>? genres = null,
        string? contentRating = null,
        string? overview = null)
    {
        Title = title;
        SortTitle = sortTitle;

        // Both follow the rule the rest of this method already follows: a snapshot overwrites a field
        // only when it supplies one. A provider that publishes no genres has not said the work has
        // none, and blanking what an earlier provider did publish would lose it on every refresh.
        if (genres is { Count: > 0 })
        {
            Genres = [.. genres];
        }

        if (contentRating is not null)
        {
            ContentRating = contentRating;
        }

        if (!string.IsNullOrWhiteSpace(overview))
        {
            Overview = overview;
        }

        if (originalLanguage is not null)
        {
            OriginalLanguage = originalLanguage;
        }

        if (year is not null)
        {
            Year = year;
        }

        if (runtimeMinutes is not null)
        {
            RuntimeMinutes = runtimeMinutes;
        }

        if (status is not null && Kind == WorkKind.Series)
        {
            Status = status.Value;
        }

        UpdateArtwork(posterUrl, backdropUrl);
        MetadataSnapshotId = snapshotId;
    }

    /// <summary>
    /// Re-points the selected artwork (⇠ Metadata's <c>MetadataArtworkSelected</c>). A url is overwritten
    /// only when provided, so a snapshot that carries no poster does not blank an existing one.
    /// </summary>
    public void UpdateArtwork(string? posterUrl, string? backdropUrl)
    {
        if (posterUrl is not null)
        {
            PosterUrl = posterUrl;
        }

        if (backdropUrl is not null)
        {
            BackdropUrl = backdropUrl;
        }
    }
}
