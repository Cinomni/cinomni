using Cinomni.Kernel.Identifiers;

namespace Cinomni.Catalog.Contracts;

/// <summary>Stable internal identity of a catalog work (UUIDv7, never derived from an external id).</summary>
public readonly record struct WorkId(Guid Value)
{
    public static WorkId New() => new(Uuid7.New());

    public override string ToString() => Value.ToString();
}

/// <summary>
/// Stable internal identity of a season of a series (UUIDv7, minted exactly like <see cref="WorkId"/>).
/// A season is a <em>catalog unit</em>: the acquisition spine correlates on this value, not on a
/// monitoring target id.
/// </summary>
public readonly record struct SeasonId(Guid Value)
{
    public static SeasonId New() => new(Uuid7.New());

    public override string ToString() => Value.ToString();
}

/// <summary>
/// Stable internal identity of an episode (UUIDv7, minted exactly like <see cref="WorkId"/>).
/// The finest-grained catalog unit: one asset may cover several of them (a multi-episode file)
/// and one of them may be covered by several assets (different qualities).
/// </summary>
public readonly record struct EpisodeId(Guid Value)
{
    public static EpisodeId New() => new(Uuid7.New());

    public override string ToString() => Value.ToString();
}

/// <summary>A work is either a flat Movie or a hierarchical Series.</summary>
public enum WorkKind
{
    Movie = 1,
    Series = 2,
}

/// <summary>
/// Release/availability status of a work. Persisted as <em>text</em> in <c>works.status</c> via
/// <c>HasConversion&lt;string&gt;()</c>, so members may only ever be APPENDED: renaming or removing
/// one corrupts every existing row. <see cref="Continuing"/> and <see cref="Ended"/> are the series
/// lifecycle states a provider reports.
/// </summary>
public enum WorkStatus
{
    Unknown = 0,
    Announced = 1,
    Released = 2,

    /// <summary>A series that is still airing new episodes (drives the short metadata refresh TTL).</summary>
    Continuing = 3,

    /// <summary>A series that has finished airing; its structure is not expected to grow.</summary>
    Ended = 4,
}

/// <summary>
/// External metadata providers a work can be linked to. Persisted as <em>text</em> in
/// <c>external_identifiers.provider</c>, so members may only ever be APPENDED. <see cref="TvMaze"/>
/// exists because a series found through TVMaze has no other external id to dedupe on, and rule-16
/// identity reuse would otherwise mint a duplicate work on every refresh.
/// </summary>
public enum MetadataProvider
{
    Tmdb = 1,
    Imdb = 2,
    Tvdb = 3,
    TvMaze = 4,
}

/// <summary>An external identifier as a value: provider + id. A work has 0..N; it is never the identity.</summary>
public sealed record ExternalId(MetadataProvider Provider, string Value);

/// <summary>
/// Minimal projection of a work exposed to other modules and the API. The rollup counters are 0 for a
/// movie, so no consumer has to branch on <see cref="Kind"/> to render a list.
/// <para>
/// <see cref="Overview"/>, <see cref="RuntimeMinutes"/> and <see cref="Genres"/> are the descriptive
/// fields copied from the applied metadata snapshot, so a viewer who may not read the snapshot itself
/// still learns what a title is. All three are trailing optionals: null (or empty) means the provider
/// has not said, never that the work has none.
/// </para>
/// </summary>
public sealed record WorkSummary(
    WorkId Id,
    WorkKind Kind,
    string Title,
    int? Year,
    WorkStatus Status,
    bool HasAsset,
    IReadOnlyList<ExternalId> ExternalIds,
    string? PosterUrl = null,
    string? BackdropUrl = null,
    Guid? MetadataSnapshotId = null,
    int EpisodeCount = 0,
    int AvailableEpisodeCount = 0,
    CollectionId? Collection = null,
    string? Overview = null,
    int? RuntimeMinutes = null,
    IReadOnlyList<string>? Genres = null);

/// <summary>
/// One page of works, with <see cref="Total"/> counting every work the viewer may see under the same filter —
/// not just the ones on this page.
/// </summary>
public sealed record WorkPage(IReadOnlyList<WorkSummary> Items, int Total, int Offset, int Limit);

/// <summary>
/// Where a work stands in the library, from its rollups: a movie (or a series whose structure has not
/// synced, with no episodes counted) is complete or absent by its asset flag; a series is complete when
/// every counted episode has a file, partial when some do, absent when none do.
/// </summary>
public enum WorkAvailability
{
    Complete = 1,
    Partial = 2,
    None = 3,
}

/// <summary>Orders the paged catalog list offers. Every one ends on the work id, so pages never overlap.</summary>
public enum WorkSort
{
    /// <summary>By sort title (leading articles dropped), A to Z.</summary>
    Title = 1,

    /// <summary>Newest release year first; undated works last.</summary>
    Year = 2,

    /// <summary>Most recently added first.</summary>
    Added = 3,
}

/// <summary>
/// The filters of the paged catalog list, all optional and combined with AND. They narrow what the viewer
/// may already see; none of them widens it. <see cref="Term"/> matches anywhere in the title, ignoring
/// case; <see cref="Genre"/> matches a provider genre label exactly.
/// </summary>
public sealed record WorkListQuery(
    CollectionId? Collection = null,
    WorkKind? Kind = null,
    string? Term = null,
    WorkAvailability? Availability = null,
    string? Genre = null,
    WorkSort Sort = WorkSort.Title);

/// <summary>How many visible works carry one genre.</summary>
public sealed record GenreCount(string Genre, int Count);

/// <summary>
/// The shape of the visible catalog under a collection filter, for building filter controls: how many
/// movies and series there are, and the genres of the works of the asked kind, most common first.
/// </summary>
public sealed record WorkFacets(int Movies, int Series, IReadOnlyList<GenreCount> Genres);

/// <summary>Page-size bounds for the paged catalog list.</summary>
public static class CatalogPaging
{
    /// <summary>Page size applied when a caller does not ask for one.</summary>
    public const int DefaultPageSize = 100;

    /// <summary>Hard ceiling: a caller asking for more gets this.</summary>
    public const int MaxPageSize = 500;

    /// <summary>Clamps a requested page size into <c>[1, <see cref="MaxPageSize"/>]</c>; below 1 reads as the default.</summary>
    public static int Clamp(int limit) => limit switch
    {
        < 1 => DefaultPageSize,
        > MaxPageSize => MaxPageSize,
        _ => limit,
    };
}

/// <summary>Minimal projection of a season of a series.</summary>
public sealed record SeasonSummary(
    SeasonId Id,
    WorkId WorkId,
    int Number,
    string? Title,
    DateOnly? AirDate,
    int? ExpectedEpisodeCount,
    string? PosterUrl);

/// <summary>
/// Minimal projection of an episode. Carries both air-date representations on purpose:
/// <see cref="AirDate"/> is the provider's published date and the key date-based release matching
/// (<c>Show.2026.07.28</c>) compares against, while <see cref="AirDateTime"/> is populated only when a
/// provider supplies a timezone-aware instant and is what the unaired gate should prefer.
/// </summary>
public sealed record EpisodeSummary(
    EpisodeId Id,
    SeasonId SeasonId,
    WorkId WorkId,
    int SeasonNumber,
    int Number,
    int? AbsoluteNumber,
    string? Title,
    DateOnly? AirDate,
    DateTimeOffset? AirDateTime,
    int? RuntimeMinutes,
    bool HasAsset);

/// <summary>
/// A season as supplied by a provider snapshot, expressed in Catalog's own vocabulary. Metadata's
/// records are translated into this at the boundary so <c>Cinomni.Catalog.Contracts</c> stays
/// Kernel-only (the ACL).
/// </summary>
public sealed record SeasonStructureInput(
    int Number,
    string? Title = null,
    DateOnly? AirDate = null,
    int? ExpectedEpisodeCount = null,
    string? PosterUrl = null);

/// <summary>An episode as supplied by a provider snapshot, in Catalog's own vocabulary.</summary>
public sealed record EpisodeStructureInput(
    int SeasonNumber,
    int Number,
    string? Title = null,
    int? AbsoluteNumber = null,
    DateOnly? AirDate = null,
    DateTimeOffset? AirDateTime = null,
    int? RuntimeMinutes = null,
    string? StillUrl = null);

/// <summary>
/// The full season/episode tree of one provider snapshot. <see cref="Provider"/> is what decides
/// whether the snapshot may renumber the work: the first provider to land claims the structure and
/// a later, different provider only enriches descriptively (see <c>Work.StructureProvider</c>).
/// An instance with no seasons and no episodes is a no-op, never a wipe.
/// </summary>
public sealed record SeriesStructure(
    Guid SnapshotId,
    string Provider,
    IReadOnlyList<SeasonStructureInput> Seasons,
    IReadOnlyList<EpisodeStructureInput> Episodes);
