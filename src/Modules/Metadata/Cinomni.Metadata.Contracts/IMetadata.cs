using Cinomni.Kernel.Results;

namespace Cinomni.Metadata.Contracts;

/// <summary>Searches external providers for content by term (behind the ACL). A live query, not persisted.</summary>
public interface IMetadataSearch
{
    /// <summary>
    /// Fans out across the providers that cover <paramref name="kind"/> and are available, in configured
    /// priority order. A provider that fails is skipped, not fatal. Nothing is persisted.
    /// <para>
    /// Succeeds with an empty list when every available provider was asked and none matched — a genuine
    /// miss. Fails with <see cref="MetadataErrors.NoProvider"/> when there was nobody to ask at all,
    /// because a caller that cannot tell those apart reports "no such title" for an installation that
    /// simply has no provider configured.
    /// </para>
    /// </summary>
    Task<Result<IReadOnlyList<MetadataCandidate>>> SearchAsync(
        string term,
        int? year,
        MetadataMediaKind kind = MetadataMediaKind.Movie,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Fetches a fresh snapshot for a work from a provider and persists it, emitting <c>MetadataRefreshed</c>
/// (or <c>MetadataRefreshFailed</c>). Idempotent per (work, provider): a redelivered trigger within the
/// refresh TTL is a no-op.
/// </summary>
public interface IMetadataRefresh
{
    Task RefreshAsync(
        Guid workId,
        string provider,
        string externalId,
        MetadataMediaKind kind = MetadataMediaKind.Movie,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Read model over the snapshots obtained. Catalog reads a snapshot here to enrich its work
/// (Catalog →i Metadata, allowed) — the ACL in action.
/// </summary>
public interface IMetadataQuery
{
    Task<MetadataSnapshot?> GetSnapshotAsync(MetadataSnapshotId id, CancellationToken cancellationToken = default);

    /// <summary>
    /// The season/episode tree of a snapshot, or <c>null</c> when the snapshot does not exist. This is a
    /// <b>separate</b> query on purpose: the structure must never join onto
    /// <see cref="GetSnapshotAsync"/>'s artwork include, which would multiply artwork by episodes on the
    /// movie detail page. Returns an empty tree for a movie snapshot.
    /// </summary>
    Task<MetadataSeriesStructure?> GetSeriesStructureAsync(MetadataSnapshotId id, CancellationToken cancellationToken = default);

    /// <summary>
    /// The most recently fetched snapshot of a work across every provider, or <c>null</c> when the work
    /// has none. Lets a caller that only knows the work id reach its metadata without holding a snapshot
    /// id — a series structure sync triggered by anything other than <c>MetadataRefreshed</c> needs it.
    /// </summary>
    Task<MetadataSnapshot?> GetLatestSnapshotForWorkAsync(Guid workId, CancellationToken cancellationToken = default);
}

/// <summary>One title a trending list published. The provider name is the adapter's own name (<c>tmdb</c>).</summary>
public sealed record TrendingTitle(string Provider, string ExternalId, string Title, int? Year, MetadataMediaKind Kind);

/// <summary>
/// A live read of an external list. Nothing is persisted here: Catalog decides whether to add a title,
/// and a second call with the same external id must be safe to repeat.
/// </summary>
public interface IMetadataLists
{
    /// <summary>
    /// The current trending titles of <paramref name="kind"/>, at most <paramref name="limit"/>. Empty
    /// when the provider is not configured or the call fails — a list that cannot be read is not a
    /// reason to add nothing permanently, and it is not a reason to throw into a scheduled job.
    /// </summary>
    Task<IReadOnlyList<TrendingTitle>> TrendingAsync(
        MetadataMediaKind kind,
        int limit,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Operator control over a snapshot's artwork: re-pick which persisted candidate is the selected
/// poster/backdrop. The change propagates to Catalog via <c>MetadataArtworkSelected</c>.
/// </summary>
public interface IMetadataArtwork
{
    Task<Result> SelectAsync(MetadataSnapshotId snapshotId, Guid artworkId, CancellationToken cancellationToken = default);
}
