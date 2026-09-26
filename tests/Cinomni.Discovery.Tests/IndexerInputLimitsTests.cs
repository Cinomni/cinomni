using Cinomni.Discovery.Application;
using Cinomni.Discovery.Contracts;
using Cinomni.Discovery.Indexers;
using Cinomni.Discovery.Indexers.Definition;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Discovery.Tests;

/// <summary>
/// What an administrator can send that does not fit: each is a named error, never the 500 an insert
/// past a column width used to be. Plus the pure checks the same block tightened — definition
/// selectors, the numbers an indexer reports, and the browser transport's deadlines.
/// </summary>
public sealed class IndexerInputLimitsTests : IAsyncLifetime
{
    private const string DefinitionTemplate = """
        {
          "schemaVersion": 1,
          "resultKind": "Torrent",
          "search": {
            "requests": [{ "contentKinds": ["Movie"], "method": "Get", "urlTemplate": "https://idx.example/search?q={{term}}" }],
            "responseFormat": "__FORMAT__",
            "rows": { "selector": "__ROWS__", "maxRows": 50 },
            "fields": {
              "title": { "selector": "__TITLE__", "attribute": "Text" },
              "downloadUrl": { "selector": "td.dl a", "attribute": "Href" }
            }
          }
        }
        """;

    private readonly FakeCatalogFetcher _fetcher = new();
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync() =>
        _provider = await DiscoveryTestHost.CreateAsync("cinomni_test_discovery_input_limits", services =>
        {
            services.AddSingleton(new FakeIndexerCatalog());
            services.AddSingleton<IIndexerClient, FakeIndexerClient>();
            services.AddSingleton<IIndexerCatalogFetcher>(_fetcher);
        });

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    private static string Definition(string rows = "tr.result", string title = "td.name a", string format = "Html") =>
        DefinitionTemplate.Replace("__ROWS__", rows, StringComparison.Ordinal)
            .Replace("__TITLE__", title, StringComparison.Ordinal)
            .Replace("__FORMAT__", format, StringComparison.Ordinal);

    [Fact]
    public async Task A_name_or_base_url_longer_than_its_column_is_a_named_error()
    {
        await using var scope = _provider.CreateAsyncScope();
        var admin = scope.ServiceProvider.GetRequiredService<IIndexerAdministration>();

        var longName = await admin.AddIndexerAsync(new string('n', 201), IndexerProtocol.Torznab, "https://idx.example/api", 1);
        var longUrl = await admin.AddIndexerAsync(
            "Long url", IndexerProtocol.Torznab, "https://idx.example/" + new string('p', 1000), 1);

        Assert.Equal("discovery.invalid_name", longName.Error.Code);
        Assert.Equal("discovery.invalid_base_url", longUrl.Error.Code);
    }

    [Fact]
    public async Task Capabilities_that_do_not_fit_are_refused_and_the_indexer_is_unchanged()
    {
        await using var scope = _provider.CreateAsyncScope();
        var admin = scope.ServiceProvider.GetRequiredService<IIndexerAdministration>();
        var added = await admin.AddIndexerAsync("Caps", IndexerProtocol.Torznab, "https://caps.example/api", 1);

        var tooMany = await admin.SetCapabilitiesAsync(
            added.Value, new IndexerCapabilities(MovieCategories: Enumerable.Range(100_000, 60).ToList()));
        var separator = await admin.SetCapabilitiesAsync(
            added.Value, new IndexerCapabilities(TvSearchParams: ["q,season"]));
        var fits = await admin.SetCapabilitiesAsync(
            added.Value, new IndexerCapabilities(MovieCategories: [2000, 2040], TvSearchParams: ["q", "season"]));

        Assert.Equal("discovery.invalid_capabilities", tooMany.Error.Code);
        Assert.Equal("discovery.invalid_capabilities", separator.Error.Code);
        Assert.True(fits.IsSuccess);
    }

    [Fact]
    public async Task A_credential_username_or_definition_name_longer_than_its_column_is_a_named_error()
    {
        await using var scope = _provider.CreateAsyncScope();
        var admin = scope.ServiceProvider.GetRequiredService<IIndexerAdministration>();
        var added = await admin.AddIndexerAsync("Creds", IndexerProtocol.Torznab, "https://creds.example/api", 1);

        var username = await admin.SetCredentialAsync(added.Value, new string('u', 201), "secret");
        var definition = await admin.UploadDefinitionAsync(new string('d', 201), Definition());

        Assert.Equal("discovery.invalid_credential", username.Error.Code);
        Assert.Equal("discovery.invalid_name", definition.Error.Code);
    }

    [Theory]
    [InlineData("tr[", "td.name a")]
    [InlineData("tr.result", "td::nonsense(")]
    public void A_css_selector_that_cannot_compile_is_refused_when_the_definition_is_parsed(string rows, string title)
    {
        var result = IndexerDefinitionParser.Parse(Definition(rows, title), strictSelectors: true);

        Assert.True(result.IsFailure);
        Assert.Equal("discovery.definition.invalid_selector", result.Error.Code);
    }

    [Fact]
    public void Json_row_paths_are_not_held_to_css_rules()
    {
        var result = IndexerDefinitionParser.Parse(
            Definition(rows: "data.items[", title: "name", format: "Json"), strictSelectors: true);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
    }

    [Fact]
    public void A_stored_definition_keeps_parsing_with_a_selector_the_upload_would_now_refuse()
    {
        // Refusing it here would take down, at the next search, an indexer that works today.
        var stored = Definition(title: "td::nonsense(");

        Assert.True(IndexerDefinitionParser.Parse(stored).IsSuccess);
        Assert.True(IndexerDefinitionParser.Parse(stored, strictSelectors: true).IsFailure);
    }

    [Fact]
    public async Task Uploading_a_definition_with_a_broken_selector_is_a_named_error()
    {
        await using var scope = _provider.CreateAsyncScope();
        var admin = scope.ServiceProvider.GetRequiredService<IIndexerAdministration>();

        var uploaded = await admin.UploadDefinitionAsync("Broken", Definition(rows: "tr["));

        Assert.Equal("discovery.definition.invalid_selector", uploaded.Error.Code);
    }

    [Fact]
    public void A_selector_nested_past_the_limit_is_refused_before_it_reaches_the_compiler()
    {
        // AngleSharp compiles :not(...) recursively, and a stack overflow cannot be caught.
        var deep = string.Concat(Enumerable.Repeat(":not(", 50)) + "a" + new string(')', 50);

        Assert.False(CssSelectors.IsValid(deep));
        Assert.False(CssSelectors.IsValid("a" + new string(' ', CssSelectors.MaxLength)));
        Assert.True(CssSelectors.IsValid("tr.result:not(.ad) td > a[href]"));
    }

    [Fact]
    public async Task A_catalog_base_url_is_measured_as_it_will_be_stored()
    {
        // Every stray '%' is escaped when the URL is normalised, so the typed length is not the stored one.
        var url = "https://indexer.example/" + string.Concat(Enumerable.Repeat("%zz", 320));
        Assert.True(url.Length <= 1000);
        Assert.True(new Uri(url).ToString().Length > 1000);

        _fetcher.Serve(CatalogManifests.SourceUrl, CatalogManifests.Manifest(CatalogManifests.Entry("example")));
        await using var scope = _provider.CreateAsyncScope();
        var source = await scope.ServiceProvider.GetRequiredService<IIndexerCatalogSources>()
            .AddSourceAsync("Example", CatalogManifests.SourceUrl);
        var admin = scope.ServiceProvider.GetRequiredService<IIndexerAdministration>();
        var installed = await admin.InstallCatalogIndexerAsync(source.Value.Id, "example", null, url, null, null);
        var added = await admin.AddIndexerAsync("Percent", IndexerProtocol.Torznab, url.Replace("indexer.example", "idx.example"), 1);

        Assert.Equal("discovery.invalid_base_url", installed.Error.Code);
        Assert.Equal("discovery.invalid_base_url", added.Error.Code);
    }

    [Fact]
    public void A_negative_size_or_seeder_count_is_zero()
    {
        var candidate = new ReleaseCandidate(
            "guid", "Title", "magnet:?xt=urn:btih:abc", ReleaseProtocol.Torrent, SizeBytes: -5, Seeders: -1, null, "idx");

        var storable = ReleaseSearch.ToStorable(candidate);

        Assert.Equal(0, storable!.SizeBytes);
        Assert.Equal(0, storable.Seeders); // zero, so an indexer's minimum still refuses it
    }

    [Fact]
    public void The_browser_waits_longer_than_the_solve_it_allows()
    {
        // Both were 30 seconds, so a challenge that used its whole allowance was always cut off here.
        Assert.True(FlareSolverrClient.RequestTimeout > FlareSolverrClient.ChallengeTimeout);
    }
}
