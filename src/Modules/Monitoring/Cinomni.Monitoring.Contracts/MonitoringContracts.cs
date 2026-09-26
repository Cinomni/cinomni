using Cinomni.Catalog.Contracts;
using Cinomni.Kernel.Identifiers;
using Cinomni.Search.Contracts;

namespace Cinomni.Monitoring.Contracts;

/// <summary>Stable internal identity of a monitored target (UUIDv7).</summary>
public readonly record struct MonitoredTargetId(Guid Value)
{
    public static MonitoredTargetId New() => new(Uuid7.New());

    public override string ToString() => Value.ToString();
}

/// <summary>
/// The unit that can be monitored. A movie is the degenerate case of the hierarchy; a series
/// is a <see cref="Series"/> policy root with <see cref="Season"/> children and
/// <see cref="Episode"/> grandchildren.
/// <para>
/// Persisted as <em>text</em> in <c>monitored_targets.kind</c> via <c>HasConversion&lt;string&gt;()</c>,
/// so members may only ever be APPENDED: renaming or removing one corrupts every existing row.
/// </para>
/// </summary>
public enum TargetKind
{
    Movie = 1,
    Season = 2,
    Episode = 3,

    /// <summary>
    /// The policy root of a series. It holds the mode, parents the season targets and rolls their
    /// missing state up — but it is <b>never acquirable</b>: no search and no acquisition intent is
    /// ever opened for it, or enabling a 200-episode show would open an intent for the show itself.
    /// </summary>
    Series = 4,
}

/// <summary>
/// Monitoring policy applied to a work: which of its units the platform watches.
/// <para>
/// Members may only ever be APPENDED with an explicit value. <see cref="None"/> and <see cref="All"/>
/// are the two modes the movie slice shipped and their numeric values are part of the persisted
/// contract; renumbering them would silently reinterpret every existing <c>monitored_targets</c> row
/// (and every stored <c>ApplyMonitoringPolicyCommand</c> payload, which serializes the enum).
/// </para>
/// </summary>
public enum MonitoringMode
{
    /// <summary>Not watched: no searches are scheduled for the target.</summary>
    None = 0,

    /// <summary>Watched: every unit of the work is eligible for missing/upgrade searches.</summary>
    All = 1,

    /// <summary>Only episodes that have not aired yet — the "from now on" subscription.</summary>
    Future = 2,

    /// <summary>Only the very first episode (S01E01), to sample a series before committing to it.</summary>
    Pilot = 3,

    /// <summary>Only the lowest-numbered real season (season 0 holds specials and is not "first").</summary>
    FirstSeason = 4,

    /// <summary>Only the highest-numbered real season — the currently airing one.</summary>
    LastSeason = 5,

    /// <summary>Only episodes that have already aired; new ones are not chased.</summary>
    Existing = 6,
}

/// <summary>Bounds for the paginated monitoring read model — the list routes were unbounded before.</summary>
public static class MonitoringPaging
{
    /// <summary>Page size applied when a caller does not ask for one.</summary>
    public const int DefaultPageSize = 100;

    /// <summary>Hard ceiling: a caller asking for more gets this. A series has thousands of targets.</summary>
    public const int MaxPageSize = 500;

    /// <summary>Clamps a requested page size into <c>[1, <see cref="MaxPageSize"/>]</c>.</summary>
    public static int Clamp(int limit) => limit switch
    {
        < 1 => DefaultPageSize,
        > MaxPageSize => MaxPageSize,
        _ => limit,
    };
}

/// <summary>
/// Minimal projection of a monitored target exposed to other modules and the API.
/// <para>
/// Everything after <c>IsMissing</c> is a trailing optional so the movie-era shape is untouched:
/// <see cref="TargetRef"/> is the <em>catalog unit id</em> the target watches (the work id for a
/// movie, the season/episode id for a series leaf) and is the join back to Catalog;
/// <see cref="MissingCount"/>/<see cref="TotalCount"/> are the subtree rollups the tree route renders
/// and are 0/0 for a leaf.
/// </para>
/// </summary>
public sealed record MonitoredTargetSummary(
    MonitoredTargetId Id,
    WorkId WorkId,
    TargetKind Kind,
    bool Monitored,
    MonitoringMode Mode,
    bool IsMissing,
    Guid TargetRef = default,
    MonitoredTargetId? ParentId = null,
    int? SeasonNumber = null,
    int? EpisodeNumber = null,
    int? AbsoluteNumber = null,
    DateTimeOffset? AirDate = null,
    string? Title = null,
    int MissingCount = 0,
    int TotalCount = 0,
    DateOnly? PublishedAirDate = null);

/// <summary>
/// What a search for one target would ask for: the neutral criterion Monitoring resolves from the
/// catalog, the work it belongs to, and the catalog units the search is trying to acquire. It is the
/// scheduled sweep's own answer, published so a caller can run that search on demand without
/// rebuilding — or quietly diverging from — the criterion the sweep uses.
/// </summary>
/// <param name="UnitIds">
/// The units the search is for: the work id for a movie, the episode id for an episode, and the
/// season's missing (or, when none is missing, all of its) episode ids for a season pack.
/// </param>
/// <param name="Label">A human-readable name for what is being searched ("Season 2", "S02E05").</param>
public sealed record TargetSearchPlan(
    MonitoredTargetId TargetId,
    WorkId WorkId,
    TargetKind Kind,
    SearchCriterion Criterion,
    IReadOnlyList<Guid> UnitIds,
    string Label);
