using Cinomni.Discovery.Contracts;
using Cinomni.Search.Contracts;

namespace Cinomni.Discovery.Indexers;

/// <summary>
/// The single <see cref="IIndexerClient"/> <c>ReleaseSearch</c> depends on: dispatches to the
/// adapter for an indexer's protocol so <c>ReleaseSearch.SearchOneAsync</c> stays protocol-blind,
/// exactly as it was before the <see cref="IndexerProtocol.Definition"/> protocol existed.
/// </summary>
internal sealed class IndexerClientFactory(
    TorznabIndexerClient torznabClient,
    DefinitionIndexerClient definitionClient)
    : IIndexerClient
{
    public Task<IReadOnlyList<ReleaseCandidate>> SearchAsync(
        IndexerSummary indexer,
        IndexerCredential? credential,
        string? definitionContent,
        SearchCriterion criterion,
        CancellationToken cancellationToken = default) =>
        indexer.Protocol switch
        {
            IndexerProtocol.Definition =>
                definitionClient.SearchAsync(indexer, credential, definitionContent, criterion, cancellationToken),
            _ => torznabClient.SearchAsync(indexer, credential, definitionContent, criterion, cancellationToken),
        };
}
