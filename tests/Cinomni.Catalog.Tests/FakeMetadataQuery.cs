using System.Collections.Concurrent;
using Cinomni.Kernel.Identifiers;
using Cinomni.Metadata.Contracts;

namespace Cinomni.Catalog.Tests;

/// <summary>
/// In-memory stand-in for Metadata's read model. Catalog's host registers Operations and Catalog only, so
/// every handler that reaches across the ACL (<c>AttachMetadataSnapshot</c>,
/// <c>SyncSeriesStructure</c>) needs one of these to be resolvable at all.
/// <para>
/// It reproduces the two shapes the real query returns and that the structure handler must treat as
/// no-ops: <c>null</c> for a snapshot that does not exist, and an <b>empty</b> tree for a movie snapshot.
/// </para>
/// </summary>
internal sealed class FakeMetadataQuery : IMetadataQuery
{
    private readonly ConcurrentDictionary<Guid, MetadataSnapshot> _snapshots = new();
    private readonly ConcurrentDictionary<Guid, MetadataSeriesStructure> _structures = new();

    /// <summary>Records a series snapshot and its season/episode tree, returning the snapshot id.</summary>
    public MetadataSnapshotId AddSeries(
        Guid workId,
        string provider,
        string title,
        IReadOnlyList<MetadataSeason> seasons,
        IReadOnlyList<MetadataEpisode> episodes,
        SeriesStatus? status = null)
    {
        var id = MetadataSnapshotId.New();
        _snapshots[id.Value] = NewSnapshot(id, workId, provider, title, MetadataMediaKind.Series, status);
        _structures[id.Value] = new MetadataSeriesStructure(id, seasons, episodes);
        return id;
    }

    /// <summary>Records a movie snapshot: it exists, but its structure comes back empty.</summary>
    public MetadataSnapshotId AddMovie(
        Guid workId,
        string provider,
        string title,
        IReadOnlyList<string>? genres = null,
        string? contentRating = null)
    {
        var id = MetadataSnapshotId.New();
        _snapshots[id.Value] = NewSnapshot(
            id, workId, provider, title, MetadataMediaKind.Movie, genres: genres, contentRating: contentRating);
        _structures[id.Value] = new MetadataSeriesStructure(id, [], []);
        return id;
    }

    public Task<MetadataSnapshot?> GetSnapshotAsync(
        MetadataSnapshotId id,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_snapshots.TryGetValue(id.Value, out var snapshot) ? snapshot : null);

    public Task<MetadataSeriesStructure?> GetSeriesStructureAsync(
        MetadataSnapshotId id,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_structures.TryGetValue(id.Value, out var structure) ? structure : null);

    public Task<MetadataSnapshot?> GetLatestSnapshotForWorkAsync(
        Guid workId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_snapshots.Values
            .Where(s => s.WorkId == workId)
            .OrderByDescending(s => s.FetchedAt)
            .FirstOrDefault());

    private static MetadataSnapshot NewSnapshot(
        MetadataSnapshotId id,
        Guid workId,
        string provider,
        string title,
        MetadataMediaKind kind,
        SeriesStatus? status = null,
        IReadOnlyList<string>? genres = null,
        string? contentRating = null) => new(
        id,
        workId,
        provider,
        kind,
        ExternalId: Uuid7.New().ToString(),
        Title: title,
        OriginalTitle: null,
        Year: null,
        Overview: null,
        RuntimeMinutes: null,
        OriginalLanguage: null,
        PosterUrl: null,
        BackdropUrl: null,
        FetchedAt: DateTimeOffset.UtcNow,
        Artwork: [],
        SeriesStatus: status,
        Genres: genres,
        ContentRating: contentRating);
}
