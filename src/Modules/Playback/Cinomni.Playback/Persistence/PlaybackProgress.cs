namespace Cinomni.Playback.Persistence;

/// <summary>
/// Resume state for a (user, asset) pair — the "continue watching" record (one
/// upserted row per pair, not history). Applies the resume thresholds:
/// under 5% of the runtime resets to the start, past 90% marks the item watched.
/// </summary>
public sealed class PlaybackProgress
{
    private const double ResumeFloor = 0.05;
    private const double WatchedThreshold = 0.90;

    public Guid UserId { get; init; }

    public Guid AssetId { get; init; }

    /// <summary>
    /// The catalog work the asset belongs to, so "continue watching" and a per-season watched-flag
    /// lookup are answerable inside the playback schema instead of joining Library for every row.
    /// Nullable because rows written before the series slice have no correlation; the next progress
    /// report stamps them (see <see cref="Correlate"/>).
    /// </summary>
    public Guid? WorkId { get; private set; }

    /// <summary>
    /// The catalog unit the asset plays — the work id for a movie, an episode id for a series file.
    /// <para>
    /// A multi-episode file is one asset serving several units, and
    /// progress is keyed by asset, so this holds the <em>first</em> unit only. Playback of such a file
    /// always starts at the beginning: <c>PlaybackTicket.ResumePositionTicks</c> is a single offset into
    /// one file and cannot express "play episode 2 of this file". Per-episode offsets are explicitly
    /// deferred — solving them needs start/end offsets on the session, not another column here.
    /// </para>
    /// </summary>
    public Guid? UnitId { get; private set; }

    public long PositionTicks { get; private set; }

    /// <summary>
    /// The runtime the player last reported, so a resume position can be shown as a share of the whole.
    /// 0 until a report carries one; a report without one keeps the last known value.
    /// </summary>
    public long DurationTicks { get; private set; }

    public bool Played { get; private set; }

    public int PlayCount { get; private set; }

    public int? AudioStreamIndex { get; private set; }

    public int? SubtitleStreamIndex { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static PlaybackProgress Create(Guid userId, Guid assetId, DateTimeOffset now) => new()
    {
        UserId = userId,
        AssetId = assetId,
        UpdatedAt = now,
    };

    /// <summary>
    /// Records the latest position, applying the resume thresholds. A position under the floor resets
    /// to 0 (start over); past the watched threshold flags the item as played.
    /// </summary>
    public void Record(long positionTicks, long durationTicks, int? audioStreamIndex, int? subtitleStreamIndex, DateTimeOffset now)
    {
        var ratio = durationTicks > 0 ? (double)positionTicks / durationTicks : 0;
        PositionTicks = ratio < ResumeFloor ? 0 : positionTicks;
        if (durationTicks > 0)
        {
            DurationTicks = durationTicks;
        }

        if (ratio >= WatchedThreshold)
        {
            Played = true;
        }

        AudioStreamIndex = audioStreamIndex;
        SubtitleStreamIndex = subtitleStreamIndex;
        UpdatedAt = now;
    }

    /// <summary>
    /// Stamps the catalog correlation the row was created without. Idempotent and never destructive: a
    /// row already correlated keeps its values, and an unknown (empty) id is ignored, so a redelivered
    /// report cannot blank a correlation the first one established.
    /// </summary>
    public void Correlate(Guid? workId, Guid? unitId)
    {
        if (WorkId is null && workId is { } work && work != Guid.Empty)
        {
            WorkId = work;
        }

        if (UnitId is null && unitId is { } unit && unit != Guid.Empty)
        {
            UnitId = unit;
        }
    }

    /// <summary>Counts a completed watch (session reached the end / was stopped as watched).</summary>
    public void CountPlay(DateTimeOffset now)
    {
        PlayCount += 1;
        Played = true;
        UpdatedAt = now;
    }
}
