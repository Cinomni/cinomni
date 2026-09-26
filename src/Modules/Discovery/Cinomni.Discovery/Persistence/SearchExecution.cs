namespace Cinomni.Discovery.Persistence;

/// <summary>
/// One federated search run. An ephemeral work record: the table is range-partitioned by month on
/// <see cref="StartedAt"/> and whole partitions are dropped once they age out of the search window
/// (<c>discovery.retention</c>), which is why the primary key is <c>(id, started_at)</c> — every
/// unique constraint on a partitioned table has to contain the partition key.
/// <para>
/// The numbering and correlation columns record <em>what was asked for</em>. Decision reads them
/// back through <c>IReleaseSearchResults.GetRequestContextAsync</c> to check that a candidate is
/// actually the thing that was requested; it must never query this table itself.
/// </para>
/// </summary>
public sealed class SearchExecution
{
    public Guid Id { get; init; }

    public required string Term { get; init; }

    public int? Year { get; init; }

    public required string ContentKind { get; init; }

    /// <summary>The monitored target that asked, when the search came from the sweep (opaque here).</summary>
    public Guid? TargetId { get; init; }

    /// <summary>The catalog work that was searched for, when known (opaque correlation).</summary>
    public Guid? WorkId { get; init; }

    public int? SeasonNumber { get; init; }

    public int? EpisodeNumber { get; init; }

    public int? AbsoluteNumber { get; init; }

    public DateOnly? AirDate { get; init; }

    public string? TvdbId { get; init; }

    public string? ImdbId { get; init; }

    /// <summary>
    /// The catalog units the search was trying to acquire, stored as a plain uuid array. Read whole
    /// and never queried by element, so an array column beats a child table here.
    /// </summary>
    public Guid[] RequestedUnitIds { get; init; } = [];

    /// <summary>
    /// When the run began. Also the range-partition key, so it is part of the primary key and must
    /// never be updated after insert (PostgreSQL would have to move the row between partitions).
    /// </summary>
    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset CompletedAt { get; set; }

    public int ResultCount { get; set; }
}
