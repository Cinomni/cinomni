using Cinomni.Discovery.Contracts;
using Cinomni.Discovery.Persistence;
using Cinomni.Search.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Discovery.Tests;

/// <summary>
/// Enable, priority and delete are the controls a search already depended on and an operator could
/// not reach. A disabled indexer must not be asked. Deleting one removes its credential and session
/// and leaves a definition that another indexer still references.
/// </summary>
public sealed class IndexerLifecycleTests : IAsyncLifetime
{
    private const string ValidDefinition = """
        {
          "schemaVersion": 1,
          "resultKind": "Torrent",
          "search": {
            "requests": [{ "contentKinds": ["Movie"], "method": "Get", "urlTemplate": "https://idx.example/search?q={{term}}" }],
            "responseFormat": "Html",
            "rows": { "selector": "tr.result", "maxRows": 50 },
            "fields": {
              "title": { "selector": "td.name a", "attribute": "Text" },
              "downloadUrl": { "selector": "td.dl a", "attribute": "Href" }
            }
          }
        }
        """;

    private readonly FakeIndexerCatalog _indexers = new();
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync() =>
        _provider = await DiscoveryTestHost.CreateAsync("cinomni_test_discovery_indexer_lifecycle", services =>
        {
            services.AddSingleton(_indexers);
            services.AddSingleton<Indexers.IIndexerClient, FakeIndexerClient>();
        });

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task A_disabled_indexer_is_not_asked()
    {
        await using (var scope = _provider.CreateAsyncScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<IIndexerAdministration>();
            var open = await admin.AddIndexerAsync("Open lifecycle", IndexerProtocol.Torznab, "https://open.example/torznab", 1);
            var closed = await admin.AddIndexerAsync("Closed lifecycle", IndexerProtocol.Torznab, "https://closed.example/torznab", 2);
            Assert.True(open.IsSuccess, open.IsFailure ? open.Error.Message : null);
            Assert.True(closed.IsSuccess, closed.IsFailure ? closed.Error.Message : null);
            Assert.True((await admin.SetEnabledAsync(closed.Value, false)).IsSuccess);
        }

        await using (var scope = _provider.CreateAsyncScope())
        {
            var search = scope.ServiceProvider.GetRequiredService<IReleaseSearch>();
            await search.SearchAsync(new SearchCriterion("Interstellar", 2014, null, null, "Movie"));
        }

        Assert.Contains(_indexers.Queries, query => query.IndexerName == "Open lifecycle");
        Assert.DoesNotContain(_indexers.Queries, query => query.IndexerName == "Closed lifecycle");
    }

    [Fact]
    public async Task Delete_removes_the_indexer_and_keeps_a_shared_definition()
    {
        IndexerId removed;
        IndexerId kept;
        IndexerDefinitionId definitionId;

        await using (var scope = _provider.CreateAsyncScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<IIndexerAdministration>();
            var uploaded = await admin.UploadDefinitionAsync("Shared", ValidDefinition);
            Assert.True(uploaded.IsSuccess, uploaded.IsFailure ? uploaded.Error.Message : null);
            definitionId = uploaded.Value;

            var first = await admin.AddIndexerAsync(
                "First", IndexerProtocol.Definition, "https://first.example", 1, definitionId);
            var second = await admin.AddIndexerAsync(
                "Second", IndexerProtocol.Definition, "https://second.example", 2, definitionId);
            Assert.True(first.IsSuccess, first.IsFailure ? first.Error.Message : null);
            Assert.True(second.IsSuccess, second.IsFailure ? second.Error.Message : null);
            removed = first.Value;
            kept = second.Value;

            var deleted = await admin.DeleteIndexerAsync(removed);
            Assert.True(deleted.IsSuccess, deleted.IsFailure ? deleted.Error.Message : null);
            Assert.Equal("discovery.indexer_not_found", (await admin.DeleteIndexerAsync(removed)).Error.Code);
            Assert.Equal("discovery.invalid_priority", (await admin.SetPriorityAsync(kept, 0)).Error.Code);
        }

        await using var verify = _provider.CreateAsyncScope();
        var db = verify.ServiceProvider.GetRequiredService<DiscoveryDbContext>();
        Assert.False(await db.Indexers.AnyAsync(indexer => indexer.Id == removed.Value));
        Assert.True(await db.Indexers.AnyAsync(indexer => indexer.Id == kept.Value && indexer.DefinitionId == definitionId.Value));
        Assert.True(await db.IndexerDefinitions.AnyAsync(definition => definition.Id == definitionId.Value));
    }
}
