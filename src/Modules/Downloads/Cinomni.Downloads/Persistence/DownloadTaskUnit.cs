namespace Cinomni.Downloads.Persistence;

/// <summary>
/// One catalog unit a download serves — the work id for a movie, one row per episode a season pack
/// covers. Echoed from <c>DownloadQueued</c> and republished on <c>DownloadCompleted</c>, so Import
/// can map the files it lands to the episodes they belong to.
/// </summary>
public sealed class DownloadTaskUnit
{
    public Guid DownloadTaskId { get; init; }

    public Guid UnitId { get; init; }
}

/// <summary>
/// One acquisition attempt that is waiting on this download. Normally there is exactly one — the
/// attempt the task was created for.
/// <para>
/// A second appears when two goals independently select the <em>same</em> release: a monitored
/// season and a monitored episode inside it both resolve to one season pack, so both attempts arrive
/// with the same info-hash. Creating a second task for it is the defect this table removes: the
/// status stream is keyed by info-hash, so the duplicate task would never be advanced by anything
/// and would sit in <c>Downloading</c> forever with no timeout to rescue it. Instead the extra
/// attempt is recorded here and receives the same feedback the originating one does.
/// </para>
/// </summary>
public sealed class DownloadTaskClaim
{
    public Guid DownloadTaskId { get; init; }

    /// <summary>The acquisition attempt waiting on this download (the dedup key for an add).</summary>
    public Guid AttemptId { get; init; }

    /// <summary>The acquisition goal behind that attempt — what the feedback events are routed to.</summary>
    public Guid IntentId { get; init; }

    /// <summary>The monitored target behind that attempt (opaque correlation, echoed forward).</summary>
    public Guid TargetId { get; init; }

    /// <summary>True for the attempt the task was created for; false for one attached later.</summary>
    public bool IsOriginating { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}
