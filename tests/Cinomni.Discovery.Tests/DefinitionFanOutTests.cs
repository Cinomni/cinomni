using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Cinomni.Discovery.Contracts;
using Cinomni.Discovery.Indexers;
using Cinomni.Search.Contracts;
using Cinomni.Kernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cinomni.Discovery.Tests;

/// <summary>
/// The federated search with the <b>real</b> indexer transports wired in — only the socket is faked.
/// Every other Discovery search test substitutes an <see cref="IIndexerClient"/> that touches no
/// database, which is exactly why a definition-backed indexer colliding with its sibling on the
/// shared scoped <c>DiscoveryDbContext</c> stayed invisible: the fan-out is parallel, EF's context is
/// not thread-safe, and <c>ReleaseSearch</c> swallows the resulting exception into "zero candidates".
/// <para>
/// So this class runs the production <c>IndexerClientFactory</c> and
/// <c>DefinitionIndexerClient</c> against real PostgreSQL, with two enabled definition-backed
/// indexers, and asserts both endpoints were actually reached.
/// </para>
/// </summary>
public sealed class DefinitionFanOutTests : IAsyncLifetime
{
    private const string AlphaHost = "alpha-definition.example";
    private const string BetaHost = "beta-definition.example";

    /// <summary>A minimal valid definition; <c>__HOST__</c> is substituted per indexer.</summary>
    private const string DefinitionTemplate = """
        {
          "schemaVersion": 1,
          "resultKind": "Torrent",
          "search": {
            "requests": [{ "contentKinds": ["Movie"], "method": "Get", "urlTemplate": "https://__HOST__/search?q={{term}}" }],
            "responseFormat": "Html",
            "rows": { "selector": "tr.result", "maxRows": 50 },
            "fields": {
              "title": { "selector": "td.name a", "attribute": "Text" },
              "downloadUrl": { "selector": "td.dl a", "attribute": "Href" }
            }
          }
        }
        """;

    private readonly RoutingHttpMessageHandler _handler = new(new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [AlphaHost] = ResultPage("Interstellar 2014 1080p BluRay x264", $"https://{AlphaHost}/download/1.torrent"),
        [BetaHost] = ResultPage("Interstellar 2014 2160p WEB-DL x265", $"https://{BetaHost}/download/2.torrent"),
    });

    private readonly RecordingLoggerProvider _logs = new();

    private ServiceProvider _provider = null!;

    public async Task InitializeAsync() =>
        _provider = await DiscoveryTestHost.CreateAsync("cinomni_test_discovery_definition_fanout", services =>
        {
            services.AddSingleton<ILoggerProvider>(_logs);

            // The production adapters and the production dispatch, with the socket replaced. This is
            // the boundary the defect lived on, so it must not be faked away.
            services.AddIndexerClients();
            services.AddHttpClient<DefinitionIndexerClient>().ConfigurePrimaryHttpMessageHandler(() => _handler);
            services.AddHttpClient<TorznabIndexerClient>().ConfigurePrimaryHttpMessageHandler(() => _handler);
        });

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task Two_definition_backed_indexers_are_both_queried_in_one_search()
    {
        await using (var scope = _provider.CreateAsyncScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<IIndexerAdministration>();
            await AddDefinitionIndexerAsync(admin, "Alpha", AlphaHost, priority: 1);
            await AddDefinitionIndexerAsync(admin, "Beta", BetaHost, priority: 2);
        }

        SearchOutcome outcome;
        await using (var scope = _provider.CreateAsyncScope())
        {
            var search = scope.ServiceProvider.GetRequiredService<IReleaseSearch>();
            outcome = await search.SearchAsync(new SearchCriterion("Interstellar", 2014, null, null, "Movie"));
        }

        var queriedHosts = _handler.Requests.Select(uri => uri.Host).ToHashSet(StringComparer.Ordinal);
        Assert.Contains(AlphaHost, queriedHosts);
        Assert.Contains(BetaHost, queriedHosts);

        Assert.Equal(2, outcome.Candidates.Count);
        Assert.Contains(outcome.Candidates, c => c.IndexerName == "Alpha");
        Assert.Contains(outcome.Candidates, c => c.IndexerName == "Beta");

        // The swallowed-failure path is what hid this: no indexer may have been skipped.
        Assert.DoesNotContain(_logs.Messages, m => m.Contains("search failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_definition_backed_indexer_next_to_a_torznab_one_still_reaches_both()
    {
        await using (var scope = _provider.CreateAsyncScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<IIndexerAdministration>();
            await AddDefinitionIndexerAsync(admin, "Alpha", AlphaHost, priority: 1);
            Assert.True((await admin.AddIndexerAsync(
                "Torz", IndexerProtocol.Torznab, "https://torznab-mixed.example/api", 2)).IsSuccess);
        }

        await using (var scope = _provider.CreateAsyncScope())
        {
            var search = scope.ServiceProvider.GetRequiredService<IReleaseSearch>();
            await search.SearchAsync(new SearchCriterion("Interstellar", 2014, null, null, "Movie"));
        }

        var queriedHosts = _handler.Requests.Select(uri => uri.Host).ToHashSet(StringComparer.Ordinal);
        Assert.Contains(AlphaHost, queriedHosts);
        Assert.Contains("torznab-mixed.example", queriedHosts);
        Assert.DoesNotContain(_logs.Messages, m => m.Contains("search failed", StringComparison.Ordinal));
    }

    private static async Task AddDefinitionIndexerAsync(
        IIndexerAdministration admin, string name, string host, int priority)
    {
        var definition = await admin.UploadDefinitionAsync(
            $"{name} tracker", DefinitionTemplate.Replace("__HOST__", host, StringComparison.Ordinal));
        Assert.True(definition.IsSuccess);

        var added = await admin.AddIndexerAsync(
            name, IndexerProtocol.Definition, $"https://{host}/", priority, definition.Value);
        Assert.True(added.IsSuccess);
    }

    private static string ResultPage(string title, string downloadUrl) =>
        $"""
        <table><tr class="result">
          <td class="name"><a href="/details/{Uuid7.New()}">{title}</a></td>
          <td class="dl"><a href="{downloadUrl}">DL</a></td>
        </tr></table>
        """;

    /// <summary>
    /// Answers per request host and records every request it received. Thread-safe on purpose: the
    /// search fans out in parallel, and a <c>List&lt;T&gt;</c> here would corrupt the evidence the
    /// assertions rest on.
    /// </summary>
    private sealed class RoutingHttpMessageHandler(IReadOnlyDictionary<string, string> bodiesByHost)
        : HttpMessageHandler
    {
        private const string EmptyTorznabFeed = "<rss><channel></channel></rss>";

        public ConcurrentBag<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Add(uri);

            var (body, contentType) = bodiesByHost.TryGetValue(uri.Host, out var html)
                ? (html, "text/html")
                : (EmptyTorznabFeed, "application/xml");

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, contentType),
            });
        }
    }
}
