using Cinomni.Monitoring.Contracts;

namespace Cinomni.Monitoring.Persistence;

/// <summary>
/// The Monitoring aggregate root: a unit the platform watches on behalf of a work. A movie is
/// one row (the degenerate case of the hierarchy); a series is a self-referencing tree of
/// <c>Series</c> → <c>Season</c> → <c>Episode</c> rows linked by <see cref="ParentTargetId"/>.
/// <para>
/// <see cref="TargetRef"/> is the <b>catalog unit id</b> — the work id for a movie, the season or
/// episode id for a series node. That is the join back to Catalog and the value that travels
/// downstream as <c>UnitId</c> on the acquisition spine.
/// </para>
/// </summary>
public sealed class MonitoredTarget
{
    /// <summary>Longest episode/season title kept; providers publish some very long ones.</summary>
    public const int TitleMaxLength = 300;

    public Guid Id { get; init; }

    /// <summary>The catalog work this target belongs to (cross-context reference by id).</summary>
    public Guid WorkId { get; init; }

    public TargetKind Kind { get; init; }

    /// <summary>The catalog unit this target watches; equals <see cref="WorkId"/> for a movie and for a series root.</summary>
    public Guid TargetRef { get; init; }

    /// <summary>
    /// The target above this one in the tree: a season's series root, an episode's season. Null for a
    /// root. A plain indexed column rather than a self-referencing foreign key — the tree is built and
    /// torn down by this module alone, and a self FK would force a delete ordering on a table whose rows
    /// are only ever removed with their whole work.
    /// </summary>
    public Guid? ParentTargetId { get; set; }

    /// <summary>Whether searches are scheduled for this target.</summary>
    public bool Monitored { get; set; }

    public MonitoringMode Mode { get; set; }

    /// <summary>Whether the target still lacks a playable asset (true until Import lands one).</summary>
    public bool IsMissing { get; set; }

    /// <summary>Season number of a Season/Episode target; null for a movie or a series root.</summary>
    public int? SeasonNumber { get; set; }

    /// <summary>Episode number within <see cref="SeasonNumber"/>; null for anything but an episode.</summary>
    public int? EpisodeNumber { get; set; }

    /// <summary>Absolute (anime) episode number when the provider publishes one.</summary>
    public int? AbsoluteNumber { get; set; }

    /// <summary>
    /// When the unit airs, resolved once at materialisation:
    /// <c>AirDateTime ?? AirDate at UTC midnight</c>. The sweep compares it against <c>now</c> to skip
    /// unaired episodes, so storing the resolved instant keeps that gate a single indexed comparison.
    /// </summary>
    public DateTimeOffset? AirDate { get; set; }

    /// <summary>
    /// The air date exactly as the provider published it, the other half of the two-date model.
    /// <see cref="AirDate"/> is the tz-aware gate instant normalised to UTC, which lands on the following
    /// day for anything broadcast west of UTC; this is the date a date-numbered release
    /// ("Show.2026.07.28") is matched on, so it is carried rather than re-derived from the instant.
    /// Null when the provider published no date, and on rows materialised before this column existed.
    /// </summary>
    public DateOnly? PublishedAirDate { get; set; }

    /// <summary>Episode (or season) title, carried so the tree route renders without a Catalog round-trip.</summary>
    public string? EpisodeTitle { get; set; }

    /// <summary>Acquisition profile to apply once searching (assigned by later increments).</summary>
    public Guid? AcquisitionProfileId { get; set; }

    /// <summary>
    /// Whether what is on disk for this target is still worth bettering. Monitoring cannot work this out
    /// — it knows nothing of qualities or profiles — so Decision answers after every import and this
    /// records the answer.
    /// <para>
    /// False by default, and that is the safe direction: a target nobody has judged is left alone. The
    /// opposite default would have an upgrade sweep set off across the whole library the first time the
    /// scheduler ran after an update.
    /// </para>
    /// </summary>
    public bool UpgradeWanted { get; set; }

    /// <summary>When the last missing/upgrade search was requested — gates the search cadence.</summary>
    public DateTimeOffset? LastSearchRequestedAt { get; set; }

    /// <summary>When the missing evaluation last ran for this target.</summary>
    public DateTimeOffset? LastEvaluatedAt { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>True for the root of a work's tree — the only row <c>GetByWorkAsync</c> may return.</summary>
    public bool IsRoot => Kind is TargetKind.Movie or TargetKind.Series;

    /// <summary>
    /// True when the platform can actually acquire a file for this target. The <c>Series</c> root is a
    /// policy node only: opening an acquisition intent for it would mean "download the whole show".
    /// </summary>
    public bool IsAcquirable => Kind is not TargetKind.Series;
}
