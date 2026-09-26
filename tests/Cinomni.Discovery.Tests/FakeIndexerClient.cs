using System.Collections.Concurrent;
using Cinomni.Discovery.Contracts;
using Cinomni.Discovery.Indexers;
using Cinomni.Search.Contracts;

namespace Cinomni.Discovery.Tests;

/// <summary>
/// Canned responses per indexer name, so the orchestration (fan-out, dedup, persistence) is
/// tested without a live indexer. The real Torznab transport is covered by the parser tests.
/// <para>
/// It also <b>records what it was asked</b> — the criterion and the URL the real client would have
/// composed for it. Without that, no orchestration test can observe the query shape at all, which is
/// how a wrong TV query would look exactly like "nothing available".
/// </para>
/// </summary>
internal sealed class FakeIndexerCatalog
{
    private readonly ConcurrentDictionary<string, IReadOnlyList<ReleaseCandidate>> _byIndexer = new();

    /// <summary>Every query the fake was asked, in arrival order.</summary>
    public ConcurrentBag<RecordedQuery> Queries { get; } = [];

    public void Set(string indexerName, params ReleaseCandidate[] candidates) =>
        _byIndexer[indexerName] = candidates;

    public IReadOnlyList<ReleaseCandidate> For(string indexerName) =>
        _byIndexer.TryGetValue(indexerName, out var candidates) ? candidates : [];

    public void Record(
        IndexerSummary indexer, IndexerCredential? credential, string? definitionContent, SearchCriterion criterion) =>
        Queries.Add(new RecordedQuery(
            indexer.Name,
            criterion,
            TorznabQueryBuilder.BuildRequestUrl(indexer, criterion, credential),
            indexer,
            credential,
            definitionContent));

    /// <param name="Indexer">
    /// The whole summary the search handed the adapter, not just its name: a field the caller forgot
    /// to project onto it is invisible from the name and the URL alone, and one such omission
    /// (<see cref="IndexerSummary.DefinitionId"/>) silently disabled every definition-backed indexer.
    /// </param>
    /// <param name="Credential">
    /// What the search resolved for this indexer, so a test can tell "no credential stored" from
    /// "stored but unreadable" — both reach an adapter as null and only this records which happened.
    /// </param>
    /// <param name="DefinitionContent">
    /// The definition document the search read <em>before</em> the parallel fan-out. Null for a
    /// Torznab/Newznab indexer and for one whose definition has been deleted; an adapter must never
    /// go and fetch it itself, because the scoped DbContext is not thread-safe.
    /// </param>
    internal sealed record RecordedQuery(
        string IndexerName,
        SearchCriterion Criterion,
        string Url,
        IndexerSummary Indexer,
        IndexerCredential? Credential,
        string? DefinitionContent);
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
        catalog.Record(indexer, credential, definitionContent, criterion);
        return Task.FromResult(catalog.For(indexer.Name));
    }
}
