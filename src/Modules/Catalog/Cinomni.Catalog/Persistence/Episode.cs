namespace Cinomni.Catalog.Persistence;

/// <summary>
/// An episode: the leaf of the <c>Work (1) → Season (N) → Episode (M)</c> hierarchy and the finest-grained
/// catalog unit the acquisition spine correlates on.
/// <para>
/// <see cref="WorkId"/> and <see cref="SeasonNumber"/> are denormalised so <c>SxxEyy</c> resolution is one
/// indexed lookup with no join. <see cref="WorkId"/> is deliberately a plain indexed column and NOT a second
/// foreign key to <c>works</c> — two cascade paths to the same row is a PostgreSQL error; the cascade runs
/// <c>works → seasons → episodes</c>.
/// </para>
/// <para>
/// The natural key is <c>(WorkId, SeasonNumber, Number)</c>; a structure sync upserts on it and never deletes,
/// so provider renumbering cannot orphan an asset link or a monitored target pointing at <see cref="Id"/>.
/// </para>
/// </summary>
public sealed class Episode
{
    /// <summary>Maximum persisted length of <see cref="Title"/>.</summary>
    public const int TitleMaxLength = 500;

    /// <summary>Maximum persisted length of <see cref="StillUrl"/>.</summary>
    public const int UrlMaxLength = 1000;

    public Guid Id { get; init; }

    /// <summary>The season this episode belongs to (cascade parent).</summary>
    public Guid SeasonId { get; init; }

    /// <summary>The series, denormalised. A plain indexed column, never a foreign key (see the type remarks).</summary>
    public Guid WorkId { get; init; }

    /// <summary>The season number, denormalised so <c>SxxEyy</c> resolution needs no join. 0 means specials.</summary>
    public int SeasonNumber { get; init; }

    /// <summary>The episode number within its season.</summary>
    public int Number { get; init; }

    /// <summary>
    /// The series-wide absolute number, which only some providers publish and which anime releases are
    /// numbered by. Null for most non-anime episodes — which is why its unique index is filtered.
    /// </summary>
    public int? AbsoluteNumber { get; set; }

    /// <summary>
    /// Whether <see cref="AbsoluteNumber"/> was counted here rather than published by the provider.
    /// <para>
    /// Only TheTVDB publishes an absolute ordering, so a series catalogued from anywhere else has
    /// none — and an anime release numbered <c>053</c> then matches nothing at all. Counting the
    /// episodes in air order fills that in, and is right for most anime; it is wrong exactly where
    /// scene numbering diverges from the provider's, which is the case a mapping service exists to
    /// settle. Recording which of the two a number is means that when one turns out to be wrong,
    /// there is something to look at besides the number itself.
    /// </para>
    /// </summary>
    public bool AbsoluteNumberIsDerived { get; set; }

    public string? Title { get; set; }

    /// <summary>
    /// The air date as the provider published it. This is the <em>matching key</em>: date-based releases
    /// (<c>Show.2026.07.28</c>) are compared against it, so it must stay exactly the calendar date the
    /// provider gave, with no timezone shifting.
    /// </summary>
    public DateOnly? AirDate { get; set; }

    /// <summary>
    /// The air time as a timezone-aware instant, populated only when the provider supplies one
    /// (TVMaze's <c>airstamp</c>). This is the <em>gate</em>: unaired checks and the Future/Existing
    /// monitoring modes should prefer it and fall back to <see cref="AirDate"/> at UTC midnight.
    /// Always stored in UTC.
    /// </summary>
    public DateTimeOffset? AirDateTime { get; set; }

    public int? RuntimeMinutes { get; set; }

    /// <summary>Availability: whether this episode has a playable asset. Set through <see cref="MarkAvailable"/>.</summary>
    public bool HasAsset { get; private set; }

    public string? StillUrl { get; set; }

    /// <summary>The metadata snapshot this episode's descriptive fields came from, if any.</summary>
    public Guid? MetadataSnapshotId { get; private set; }

    public DateTimeOffset AddedAt { get; init; }

    /// <summary>
    /// Marks the episode as having a playable asset. Returns <c>false</c> when it already had one, so the
    /// caller can skip the roll-up and the event: a season-pack import is redelivered at-least-once and
    /// every episode in it must count exactly once.
    /// </summary>
    public bool MarkAvailable()
    {
        if (HasAsset)
        {
            return false;
        }

        HasAsset = true;
        return true;
    }

    /// <summary>
    /// Copies a provider snapshot's descriptive fields onto this episode (the ACL), following
    /// <c>Work.ApplyMetadata</c>'s rule: a field is overwritten only when the snapshot supplies a value.
    /// The natural key and <see cref="HasAsset"/> are never touched — a metadata refresh must not make a
    /// downloaded episode look missing again.
    /// </summary>
    public void ApplyMetadata(
        string? title,
        int? absoluteNumber,
        DateOnly? airDate,
        DateTimeOffset? airDateTime,
        int? runtimeMinutes,
        string? stillUrl,
        Guid snapshotId)
    {
        if (title is not null)
        {
            Title = Text.Truncate(title, TitleMaxLength);
        }

        if (absoluteNumber is not null)
        {
            AbsoluteNumber = absoluteNumber;
        }

        if (airDate is not null)
        {
            AirDate = airDate;
        }

        if (airDateTime is not null)
        {
            AirDateTime = airDateTime.Value.ToUniversalTime();
        }

        if (runtimeMinutes is not null)
        {
            RuntimeMinutes = runtimeMinutes;
        }

        if (stillUrl is not null)
        {
            StillUrl = Text.Truncate(stillUrl, UrlMaxLength);
        }

        MetadataSnapshotId = snapshotId;
    }
}
