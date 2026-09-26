using System.Net;
using Cinomni.Discovery.Contracts;
using Cinomni.Discovery.Indexers;
using Cinomni.Kernel.Identifiers;
using Cinomni.Search.Contracts;
using Microsoft.Extensions.Logging;

namespace Cinomni.Discovery.Tests;

/// <summary>
/// Tests for the production 'Definition' protocol adapter against a fake HTTP transport (never a
/// real site). No database: the adapter no longer reads one. Its definition is resolved by
/// <c>ReleaseSearch</c> before the parallel fan-out and handed in, because the module's scoped
/// <c>DiscoveryDbContext</c> cannot serve two indexers at once — <see cref="DefinitionFanOutTests"/>
/// is the integration test that holds that end of the contract against real PostgreSQL.
/// </summary>
public sealed class DefinitionIndexerClientTests
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

    [Fact]
    public async Task Issues_the_composed_request_and_parses_the_response()
    {
        const string html = """
            <table><tr class="result">
              <td class="name"><a href="/details/1">Interstellar 2014 1080p BluRay x264</a></td>
              <td class="dl"><a href="https://idx.example/download/1.torrent">DL</a></td>
            </tr></table>
            """;
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, html);
        var client = new DefinitionIndexerClient(new HttpClient(handler));

        var candidates = await client.SearchAsync(
            Summary(new IndexerDefinitionId(Uuid7.New())), null, ValidDefinition,
            new SearchCriterion("Interstellar", 2014, null, null, "Movie"));

        Assert.Single(candidates);
        Assert.Equal("Interstellar 2014 1080p BluRay x264", candidates[0].Title);
        Assert.Equal("https://idx.example/download/1.torrent", candidates[0].DownloadUrl);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://idx.example/search?q=Interstellar", request.ToString());
    }

    [Fact]
    public async Task Throws_when_the_indexer_carries_no_definition_reference()
    {
        var client = new DefinitionIndexerClient(new HttpClient(new FakeHttpMessageHandler(HttpStatusCode.OK, "")));

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SearchAsync(
            Summary(null), null, null, new SearchCriterion("Interstellar", 2014, null, null, "Movie")));
    }

    [Fact]
    public async Task Throws_when_the_referenced_definition_no_longer_exists()
    {
        var client = new DefinitionIndexerClient(new HttpClient(new FakeHttpMessageHandler(HttpStatusCode.OK, "")));

        // The caller resolved nothing for a DefinitionId that still names a row: the row is gone.
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SearchAsync(
            Summary(new IndexerDefinitionId(Guid.NewGuid())), null, null,
            new SearchCriterion("Interstellar", 2014, null, null, "Movie")));
    }

    [Fact]
    public async Task Throws_when_the_resolved_definition_is_not_valid()
    {
        var client = new DefinitionIndexerClient(new HttpClient(new FakeHttpMessageHandler(HttpStatusCode.OK, "")));

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SearchAsync(
            Summary(new IndexerDefinitionId(Guid.NewGuid())), null, "{ \"schemaVersion\": 99 }",
            new SearchCriterion("Interstellar", 2014, null, null, "Movie")));
    }

    [Fact]
    public async Task Returns_no_candidates_and_issues_no_request_when_no_search_request_matches_the_content_kind()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, "<table></table>");
        var client = new DefinitionIndexerClient(new HttpClient(handler));

        var candidates = await client.SearchAsync(
            Summary(new IndexerDefinitionId(Uuid7.New())), null, ValidDefinition,
            new SearchCriterion("The Wire", 2002, null, null, "Episode"));

        Assert.Empty(candidates);
        Assert.Empty(handler.Requests);
    }

    /// <summary>
    /// The search path's half of the size defect. A site whose size column stops parsing must not
    /// lose its releases — that would turn a formatting change into the loss of a whole indexer —
    /// but it must stop being silent about it, because "0 B" on every release is exactly what an
    /// indexer that publishes no size looks like. The raw value stays out of the log on purpose: a
    /// third-party response body is not log material, and the dry run is where the detail belongs.
    /// </summary>
    [Fact]
    public async Task Keeps_a_release_whose_size_rule_failed_and_logs_that_it_failed()
    {
        const string definitionWithSize = """
            {
              "schemaVersion": 1,
              "resultKind": "Torrent",
              "search": {
                "requests": [{ "contentKinds": ["Movie"], "method": "Get", "urlTemplate": "https://idx.example/search?q={{term}}" }],
                "responseFormat": "Html",
                "rows": { "selector": "tr.result", "maxRows": 50 },
                "fields": {
                  "title": { "selector": "td.name a", "attribute": "Text" },
                  "downloadUrl": { "selector": "td.dl a", "attribute": "Href", "transform": "ResolveRelativeUrl" },
                  "sizeBytes": { "selector": "td.size", "attribute": "Text", "transform": "ParseSize" }
                }
              }
            }
            """;
        const string html = """
            <table>
              <tr class="result">
                <td class="name"><a href="/details/1">Interstellar 2014 1080p</a></td>
                <td class="dl"><a href="/download/1.torrent">DL</a></td>
                <td class="size">473K</td>
              </tr>
              <tr class="result">
                <td class="name"><a href="/details/2">Interstellar 2014 720p</a></td>
                <td class="dl"><a href="/download/2.torrent">DL</a></td>
                <td class="size">n/a</td>
              </tr>
            </table>
            """;
        var recorder = new RecordingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(recorder));
        var client = new DefinitionIndexerClient(
            new HttpClient(new FakeHttpMessageHandler(HttpStatusCode.OK, html)),
            loggerFactory.CreateLogger<DefinitionIndexerClient>());

        var candidates = await client.SearchAsync(
            Summary(new IndexerDefinitionId(Uuid7.New())), null, definitionWithSize,
            new SearchCriterion("Interstellar", 2014, null, null, "Movie"));

        Assert.Equal(2, candidates.Count);
        Assert.Equal(484352L, candidates[0].SizeBytes);
        Assert.Equal(0L, candidates[1].SizeBytes);

        var warning = Assert.Single(recorder.Messages, message => message.Contains("could not be read"));
        Assert.Contains("sizeBytes:discovery.definition.transform.parse_size_failed x1", warning);
        Assert.Contains("Example", warning);
        Assert.DoesNotContain("n/a", warning);
    }

    [Fact]
    public async Task Relative_search_and_detail_urls_stay_on_origin_and_resolution_is_bounded()
    {
        const string definition = """
            {
              "schemaVersion": 1,
              "resultKind": "Torrent",
              "search": {
                "requests": [{ "contentKinds": ["Movie"], "method": "Get", "urlTemplate": "/search/{{term}}/1/" }],
                "responseFormat": "Html",
                "rows": { "selector": "tr.result", "maxRows": 50 },
                "fields": {
                  "title": { "selector": "a.title", "attribute": "Text" },
                  "downloadUrl": { "selector": "a.title", "attribute": "Href", "transform": "ResolveRelativeUrl" }
                },
                "details": {
                  "maxRequests": 2,
                  "downloadUrl": { "selector": "a[href^='magnet:']", "attribute": "Href" }
                }
              }
            }
            """;
        const string search = """
            <table>
              <tr class="result"><td><a class="title" href="/torrent/1">One</a></td></tr>
              <tr class="result"><td><a class="title" href="/torrent/2">Two</a></td></tr>
              <tr class="result"><td><a class="title" href="/torrent/3">Three</a></td></tr>
            </table>
            """;
        var handler = new RoutingHandler(search);
        var client = new DefinitionIndexerClient(new HttpClient(handler));

        var candidates = await client.SearchAsync(
            Summary(new IndexerDefinitionId(Guid.NewGuid())), null, definition,
            new SearchCriterion("Sintel", null, null, null, "Movie"));

        Assert.Equal(2, candidates.Count);
        Assert.All(candidates, candidate => Assert.StartsWith("magnet:", candidate.DownloadUrl, StringComparison.Ordinal));
        Assert.Equal(3, handler.Requests.Count);
        Assert.All(handler.Requests, uri => Assert.Equal("idx.example", uri.Host));
    }

    [Fact]
    public async Task Throws_when_every_detail_request_fails()
    {
        const string definition = """
            {
              "schemaVersion": 1,
              "resultKind": "Torrent",
              "search": {
                "requests": [{ "contentKinds": ["Movie"], "method": "Get", "urlTemplate": "/search/{{term}}/1/" }],
                "responseFormat": "Html",
                "rows": { "selector": "tr.result", "maxRows": 10 },
                "fields": {
                  "title": { "selector": "a.title", "attribute": "Text" },
                  "downloadUrl": { "selector": "a.title", "attribute": "Href", "transform": "ResolveRelativeUrl" }
                },
                "details": {
                  "maxRequests": 2,
                  "downloadUrl": { "selector": "a[href^='magnet:']", "attribute": "Href" }
                }
              }
            }
            """;
        const string search = "<table><tr class='result'><td><a class='title' href='/torrent/1'>One</a></td></tr></table>";
        var client = new DefinitionIndexerClient(new HttpClient(new RoutingHandler(search, failDetails: true)));

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => client.SearchAsync(
            Summary(new IndexerDefinitionId(Guid.NewGuid())), null, definition,
            new SearchCriterion("Sintel", null, null, null, "Movie")));

        Assert.Contains("could not resolve any release detail pages", failure.Message);
    }

    [Fact]
    public async Task Stops_asking_for_detail_pages_after_failures_in_a_row()
    {
        // Through the browser transport each failure can take a minute; twenty in a row held a browser
        // slot, and the whole search, for twenty minutes.
        const string definition = """
            {
              "schemaVersion": 1,
              "resultKind": "Torrent",
              "search": {
                "requests": [{ "contentKinds": ["Movie"], "method": "Get", "urlTemplate": "/search/{{term}}/1/" }],
                "responseFormat": "Html",
                "rows": { "selector": "tr.result", "maxRows": 50 },
                "fields": {
                  "title": { "selector": "a.title", "attribute": "Text" },
                  "downloadUrl": { "selector": "a.title", "attribute": "Href", "transform": "ResolveRelativeUrl" }
                },
                "details": {
                  "maxRequests": 10,
                  "downloadUrl": { "selector": "a[href^='magnet:']", "attribute": "Href" }
                }
              }
            }
            """;
        var rows = string.Concat(Enumerable.Range(1, 10).Select(i =>
            $"<tr class='result'><td><a class='title' href='/torrent/{i}'>Row {i}</a></td></tr>"));
        var handler = new RoutingHandler($"<table>{rows}</table>", failDetails: true);
        var client = new DefinitionIndexerClient(new HttpClient(handler));

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SearchAsync(
            Summary(new IndexerDefinitionId(Guid.NewGuid())), null, definition,
            new SearchCriterion("Sintel", null, null, null, "Movie")));

        Assert.Equal(
            DefinitionIndexerClient.MaxDetailFailuresInARow,
            handler.Requests.Count(uri => uri.AbsolutePath.StartsWith("/torrent/", StringComparison.Ordinal)));
    }

    private sealed class RoutingHandler(string searchBody, bool failDetails = false) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            var isDetail = request.RequestUri!.AbsolutePath.StartsWith("/torrent/", StringComparison.Ordinal);
            if (isDetail && failDetails)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway));
            }

            var body = isDetail
                ? $"<a href='magnet:?xt=urn:btih:{request.RequestUri.Segments[^1]}'>Magnet</a>"
                : searchBody;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    private static IndexerSummary Summary(IndexerDefinitionId? definitionId) => new(
        new IndexerId(Guid.NewGuid()), "Example", IndexerProtocol.Definition, "https://idx.example/", 1, true,
        DefinitionId: definitionId);
}
