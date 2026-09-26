using Cinomni.Discovery.Contracts;
using Cinomni.Search.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Discovery.Tests;

/// <summary>
/// Integration tests for the definition-related half of <c>IIndexerAdministration</c>, against a
/// real PostgreSQL instance: upload persists a validated definition (or fails with the parser's own
/// named error, never after the fact), validate dry-runs without persisting or touching the
/// network, and a 'Definition' protocol indexer must reference an uploaded definition.
/// </summary>
public sealed class IndexerAdministrationDefinitionTests : IAsyncLifetime
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

    private ServiceProvider _provider = null!;

    public async Task InitializeAsync() =>
        _provider = await DiscoveryTestHost.CreateAsync("cinomni_test_discovery_definitions", services =>
        {
            services.AddSingleton<FakeIndexerCatalog>();
            services.AddSingleton<Indexers.IIndexerClient, FakeIndexerClient>();
        });

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task Uploads_a_valid_definition_and_lists_it()
    {
        await using var scope = _provider.CreateAsyncScope();
        var admin = scope.ServiceProvider.GetRequiredService<IIndexerAdministration>();

        var uploaded = await admin.UploadDefinitionAsync("Example Tracker", ValidDefinition);

        Assert.True(uploaded.IsSuccess);
        var definitions = await admin.ListDefinitionsAsync();
        var definition = Assert.Single(definitions, d => d.Id == uploaded.Value);
        Assert.Equal("Example Tracker", definition.Name);
        Assert.Equal(1, definition.SchemaVersion);
        Assert.Equal(64, definition.ContentHash.Length);
    }

    /// <summary>
    /// The search path has to hand the adapter the definition to run, and it did not: every other
    /// test either built the <c>IndexerSummary</c> by hand or used a fake that ignores the field, so
    /// a definition-backed indexer threw inside the adapter, was swallowed by the per-indexer catch,
    /// and reported zero results on every search while looking healthy. Asserted here rather than in
    /// the adapter's own tests because the omission was in the caller.
    /// </summary>
    [Fact]
    public async Task A_definition_indexer_reaches_the_adapter_with_the_definition_to_run()
    {
        Guid definitionId;
        await using (var scope = _provider.CreateAsyncScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<IIndexerAdministration>();
            var uploaded = await admin.UploadDefinitionAsync("Searchable Tracker", ValidDefinition);
            Assert.True(uploaded.IsSuccess);
            definitionId = uploaded.Value.Value;

            var added = await admin.AddIndexerAsync(
                "Definition Indexer", IndexerProtocol.Definition, "https://idx.example", 1, uploaded.Value);
            Assert.True(added.IsSuccess, added.IsFailure ? added.Error.Message : null);
        }

        await using (var scope = _provider.CreateAsyncScope())
        {
            var search = scope.ServiceProvider.GetRequiredService<IReleaseSearch>();
            await search.SearchAsync(new SearchCriterion("Whatever", null, null, null, "Movie"));
        }

        var catalog = _provider.GetRequiredService<FakeIndexerCatalog>();
        var recorded = Assert.Single(catalog.Queries, q => q.IndexerName == "Definition Indexer");
        Assert.Equal(definitionId, recorded.Indexer.DefinitionId?.Value);
    }

    [Fact]
    public async Task Rejects_an_empty_name_without_persisting()
    {
        await using var scope = _provider.CreateAsyncScope();
        var admin = scope.ServiceProvider.GetRequiredService<IIndexerAdministration>();

        var result = await admin.UploadDefinitionAsync("   ", ValidDefinition);

        Assert.True(result.IsFailure);
        Assert.Equal("discovery.invalid_name", result.Error.Code);
        Assert.Empty(await admin.ListDefinitionsAsync());
    }

    [Fact]
    public async Task Rejects_a_malformed_definition_with_the_parsers_own_error_without_persisting()
    {
        await using var scope = _provider.CreateAsyncScope();
        var admin = scope.ServiceProvider.GetRequiredService<IIndexerAdministration>();

        var result = await admin.UploadDefinitionAsync("Bad", ValidDefinition.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2"));

        Assert.True(result.IsFailure);
        Assert.Equal("discovery.definition.unsupported_schema_version", result.Error.Code);
        Assert.Empty(await admin.ListDefinitionsAsync());
    }

    [Fact]
    public async Task Validates_format_only_without_a_sample_and_persists_nothing()
    {
        await using var scope = _provider.CreateAsyncScope();
        var admin = scope.ServiceProvider.GetRequiredService<IIndexerAdministration>();

        var result = await admin.ValidateDefinitionAsync(ValidDefinition, sampleResponseBody: null, sampleRequestUrl: null);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value.Candidates);
        Assert.Empty(result.Value.FieldIssues);
        Assert.Empty(await admin.ListDefinitionsAsync());
    }

    [Fact]
    public async Task Validates_and_previews_candidates_against_a_supplied_sample_response()
    {
        await using var scope = _provider.CreateAsyncScope();
        var admin = scope.ServiceProvider.GetRequiredService<IIndexerAdministration>();
        const string sample = """
            <table><tr class="result">
              <td class="name"><a href="/details/1">Interstellar 2014 1080p BluRay x264</a></td>
              <td class="dl"><a href="/download/1.torrent">DL</a></td>
            </tr></table>
            """;

        var result = await admin.ValidateDefinitionAsync(ValidDefinition, sample, "https://idx.example/search?q=Interstellar");

        Assert.True(result.IsSuccess);
        var candidate = Assert.Single(result.Value.Candidates);
        Assert.Equal("Interstellar 2014 1080p BluRay x264", candidate.Title);
        // This definition declares only title and downloadUrl, and both matched: nothing to report.
        Assert.Empty(result.Value.FieldIssues);
    }

    [Fact]
    public async Task Rejects_a_non_absolute_sample_request_url()
    {
        await using var scope = _provider.CreateAsyncScope();
        var admin = scope.ServiceProvider.GetRequiredService<IIndexerAdministration>();

        var result = await admin.ValidateDefinitionAsync(ValidDefinition, "<table></table>", "not-a-url");

        Assert.True(result.IsFailure);
        Assert.Equal("discovery.definition.validate.invalid_sample_request_url", result.Error.Code);
    }

    [Fact]
    public async Task Adding_a_definition_protocol_indexer_requires_an_existing_definition()
    {
        await using var scope = _provider.CreateAsyncScope();
        var admin = scope.ServiceProvider.GetRequiredService<IIndexerAdministration>();

        var missing = await admin.AddIndexerAsync("Example", IndexerProtocol.Definition, "https://idx.example/", 1);
        Assert.True(missing.IsFailure);
        Assert.Equal("discovery.missing_definition", missing.Error.Code);

        var bogusId = new IndexerDefinitionId(Guid.NewGuid());
        var notFound = await admin.AddIndexerAsync("Example", IndexerProtocol.Definition, "https://idx.example/", 1, bogusId);
        Assert.True(notFound.IsFailure);
        Assert.Equal("discovery.definition_not_found", notFound.Error.Code);
    }

    [Fact]
    public async Task Adding_a_non_definition_indexer_rejects_a_definition_id()
    {
        await using var scope = _provider.CreateAsyncScope();
        var admin = scope.ServiceProvider.GetRequiredService<IIndexerAdministration>();
        var uploaded = await admin.UploadDefinitionAsync("Example Tracker", ValidDefinition);

        var result = await admin.AddIndexerAsync(
            "Torznab Indexer", IndexerProtocol.Torznab, "https://idx.example/torznab", 1, uploaded.Value);

        Assert.True(result.IsFailure);
        Assert.Equal("discovery.unexpected_definition", result.Error.Code);
    }

    [Fact]
    public async Task Adding_a_definition_protocol_indexer_with_a_real_definition_succeeds_and_is_listed_with_it()
    {
        await using var scope = _provider.CreateAsyncScope();
        var admin = scope.ServiceProvider.GetRequiredService<IIndexerAdministration>();
        var uploaded = await admin.UploadDefinitionAsync("Example Tracker", ValidDefinition);

        var added = await admin.AddIndexerAsync(
            "Example Indexer", IndexerProtocol.Definition, "https://idx.example/", 1, uploaded.Value);

        Assert.True(added.IsSuccess);
        var indexers = await admin.ListIndexersAsync();
        var indexer = Assert.Single(indexers, i => i.Id == added.Value);
        Assert.Equal(IndexerProtocol.Definition, indexer.Protocol);
        Assert.Equal(uploaded.Value, indexer.DefinitionId);
    }
}
