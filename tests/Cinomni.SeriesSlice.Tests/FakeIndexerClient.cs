using System.Collections.Concurrent;
using Cinomni.Discovery.Contracts;
using Cinomni.Discovery.Indexers;
using Cinomni.Search.Contracts;

namespace Cinomni.SeriesSlice.Tests;

/// <summary>
/// Canned indexer responses plus a record of every criterion the pipeline actually asked for.
/// Indexer API keys are kept out of scope, so the acceptance suite can never reach a real
/// endpoint; recording the criterion is what lets a test assert the pipeline asked <em>once</em>, and
/// asked for the right season.
/// </summary>
internal sealed class FakeIndexerCatalog
{
    private readonly ConcurrentDictionary<string, IReadOnlyList<ReleaseCandidate>> _byIndexer = new();

    /// <summary>Every criterion the fake was asked, in arrival order.</summary>
    public ConcurrentBag<SearchCriterion> Queries { get; } = [];

    public void Set(string indexerName, params ReleaseCandidate[] candidates) =>
        _byIndexer[indexerName] = candidates;

    public IReadOnlyList<ReleaseCandidate> For(string indexerName) =>
        _byIndexer.TryGetValue(indexerName, out var candidates) ? candidates : [];

    /// <summary>A torrent release of the given title, sized like a real episode or pack.</summary>
    public static ReleaseCandidate Release(string guid, string title, long sizeBytes, string indexerName) =>
        new(guid, title, $"magnet:?xt=urn:btih:{guid}", ReleaseProtocol.Torrent, sizeBytes, 50, null, indexerName);
}

internal sealed class FakeIndexerClient(FakeIndexerCatalog catalog) : IIndexerClient
{
    public Task<IReadOnlyList<ReleaseCandidate>> SearchAsync(
        IndexerSummary indexer,
        IndexerCredential? credential,
        string? definitionContent,
        SearchCriterion criterion,
        CancellationToken cancellationToken = default)
    {
        catalog.Queries.Add(criterion);
        return Task.FromResult(catalog.For(indexer.Name));
    }
}
