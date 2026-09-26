using Cinomni.Discovery.Contracts;

namespace Cinomni.Discovery.Persistence;

/// <summary>
/// A single raw release persisted from a search. Discovery keeps it unparsed (title as-is);
/// Release Parsing derives the canonical identity later. Ephemeral: range-partitioned by month on
/// <see cref="FoundAt"/>, co-partitioned with its execution so both months are dropped together.
/// </summary>
public sealed class SearchResult
{
    public const int ReleaseGuidMaxLength = 500;

    public const int TitleMaxLength = 1000;

    public const int DownloadUrlMaxLength = 2000;

    public const int TvdbIdMaxLength = 50;

    public const int CategoryMaxLength = 50;

    public Guid Id { get; init; }

    public Guid ExecutionId { get; init; }

    /// <summary>
    /// The instant its search started, copied from the parent execution. It exists because a
    /// partitioned table needs its own partition key: the row cannot be reached through a foreign
    /// key across partitions, so it carries the time reference itself. Part of the primary key and
    /// never updated after insert.
    /// <para>
    /// <b>Invariant: this must equal the parent's <c>StartedAt</c> exactly — never "now".</b> It is
    /// what makes a result and its execution land in the same month, and dropping a month is what
    /// replaced the foreign key. Setting it to the moment the row was written would split a search
    /// across two partitions near a month boundary, and retention would then drop the execution while
    /// its results survived (or the reverse) with nothing left in the schema to catch it.
    /// <c>ReleaseSearch</c> is the only writer, and <c>DiscoverySearchTests</c> pins the equality.
    /// </para>
    /// </summary>
    public DateTimeOffset FoundAt { get; init; }

    /// <summary>The indexer-supplied guid — the identity used to deduplicate across indexers.</summary>
    public required string ReleaseGuid { get; init; }

    public required string Title { get; init; }

    public required string DownloadUrl { get; init; }

    public ReleaseProtocol Protocol { get; init; }

    public long SizeBytes { get; init; }

    public int? Seeders { get; init; }

    /// <summary>Peers downloading without a complete copy, when the indexer said so.</summary>
    public int? Leechers { get; init; }

    public DateTimeOffset? PublishedAt { get; init; }

    public required string IndexerName { get; init; }

    /// <summary>
    /// The indexer that returned the release. What a result is attributed to when it matters who
    /// offered it — a name is the operator's label and can be renamed or reused, an id cannot. Null
    /// on rows stored before it was recorded, which therefore prove nothing about their origin.
    /// </summary>
    public Guid? IndexerId { get; init; }

    /// <summary>Season the indexer attributed to the release (from its feed attributes), when it said so.</summary>
    public int? SeasonNumber { get; init; }

    /// <summary>Episode the indexer attributed to the release, when it said so.</summary>
    public int? EpisodeNumber { get; init; }

    /// <summary>TheTVDB series id the indexer attributed to the release, when it said so.</summary>
    public string? TvdbId { get; init; }

    /// <summary>The indexer's own category id for the release, when it said so.</summary>
    public string? Category { get; init; }
}
