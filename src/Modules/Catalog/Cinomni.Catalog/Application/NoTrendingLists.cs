using Cinomni.Metadata.Contracts;

namespace Cinomni.Catalog.Application;

/// <summary>
/// The list a composition has when Metadata's adapters are not registered. Returning empty is the
/// safe answer: the scheduled job then adds nothing, which is also what an opted-out installation does.
/// </summary>
public sealed class NoTrendingLists : IMetadataLists
{
    public Task<IReadOnlyList<TrendingTitle>> TrendingAsync(
        MetadataMediaKind kind,
        int limit,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TrendingTitle>>([]);
}
