using System.Net;
using Cinomni.Discovery.Contracts;
using Cinomni.Discovery.Indexers;
using Cinomni.Kernel.Identifiers;
using Cinomni.Search.Contracts;

namespace Cinomni.Discovery.Tests;

/// <summary>
/// Tests for the dispatch <c>ReleaseSearch</c> actually depends on: a Torznab/Newznab indexer must
/// never reach the definition transport and vice versa. Neither adapter touches the database — the
/// definition is resolved before the fan-out and passed in — so this needs no PostgreSQL host.
/// </summary>
public sealed class IndexerClientFactoryTests
{
    private const string ValidDefinition = """
        {
          "schemaVersion": 1,
          "resultKind": "Torrent",
          "search": {
            "requests": [{ "contentKinds": ["Movie"], "method": "Get", "urlTemplate": "https://beta.example/search?q={{term}}" }],
            "responseFormat": "Html",
            "rows": { "selector": "tr.result", "maxRows": 50 },
            "fields": {
              "title": { "selector": "td.name a", "attribute": "Text" },
              "downloadUrl": { "selector": "td.dl a", "attribute": "Href" }
            }
          }
        }
        """;

    [Fact]
    public async Task A_torznab_indexer_reaches_only_the_torznab_transport()
    {
        var (factory, torznabHandler, definitionHandler) = BuildFactory();
        var indexer = new IndexerSummary(
            new IndexerId(Guid.NewGuid()), "Alpha", IndexerProtocol.Torznab, "https://alpha.example/torznab", 1, true);

        await factory.SearchAsync(indexer, null, null, new SearchCriterion("Interstellar", 2014, null, null, "Movie"));

        Assert.Single(torznabHandler.Requests);
        Assert.Empty(definitionHandler.Requests);
    }

    [Fact]
    public async Task A_definition_indexer_reaches_only_the_definition_transport()
    {
        var (factory, torznabHandler, definitionHandler) = BuildFactory();
        var indexer = new IndexerSummary(
            new IndexerId(Guid.NewGuid()), "Beta", IndexerProtocol.Definition, "https://beta.example/", 1, true,
            DefinitionId: new IndexerDefinitionId(Uuid7.New()));

        await factory.SearchAsync(
            indexer, null, ValidDefinition, new SearchCriterion("Interstellar", 2014, null, null, "Movie"));

        Assert.Empty(torznabHandler.Requests);
        Assert.Single(definitionHandler.Requests);
    }

    private static (IndexerClientFactory Factory, FakeHttpMessageHandler TorznabHandler, FakeHttpMessageHandler DefinitionHandler)
        BuildFactory()
    {
        var torznabHandler = new FakeHttpMessageHandler(HttpStatusCode.OK, "<rss><channel></channel></rss>", "application/xml");
        var definitionHandler = new FakeHttpMessageHandler(HttpStatusCode.OK, "<table></table>");
        var factory = new IndexerClientFactory(
            new TorznabIndexerClient(new HttpClient(torznabHandler)),
            new DefinitionIndexerClient(new HttpClient(definitionHandler)));
        return (factory, torznabHandler, definitionHandler);
    }
}
