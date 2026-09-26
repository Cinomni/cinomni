using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Cinomni.Discovery.Application;
using Cinomni.Identity.Application;
using Cinomni.Kernel.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Host.Tests;

/// <summary>
/// The catalog-source routes as the web client calls them: every field it reads is asserted on the
/// JSON body, not on the service behind the projection. Cinomni ships no source; the manifests here
/// name only reserved example hosts.
/// </summary>
[Trait("Category", "RequiresDatabase")]
public sealed class DiscoveryIndexerCatalogHttpTests : IAsyncLifetime
{
    private const string SourceUrl = "https://catalog.example.org/cinomni/indexers.json";

    private const string Definition = """
        {
          "schemaVersion": 1,
          "resultKind": "Torrent",
          "search": {
            "requests": [ { "contentKinds": ["Movie", "Series"], "method": "Get", "urlTemplate": "/search?q={{term}}" } ],
            "responseFormat": "Html",
            "rows": { "selector": "table.results tr", "maxRows": 50 },
            "fields": {
              "title": { "selector": "td.name a", "attribute": "Text" },
              "downloadUrl": { "selector": "a[href^='magnet:']", "attribute": "Href" }
            }
          }
        }
        """;

    private readonly ManifestServer _manifests = new();
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _app = await DiscoveryDefinitionHttpTestHost.StartAsync(
            "cinomni_test_discovery_catalog_http",
            configure: services => services.AddSingleton<IIndexerCatalogFetcher>(_manifests));
        _client = _app.GetTestClient();
        await using var scope = _app.Services.CreateAsyncScope();
        var admin = await scope.ServiceProvider.GetRequiredService<IUserProvisioning>()
            .CreateAdminAsync("operator", "correct horse battery staple");
        var token = (await scope.ServiceProvider.GetRequiredService<ISessionService>().IssueAsync(admin.Value)).Token;
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task A_fresh_installation_has_no_source_and_an_empty_catalog()
    {
        Assert.Empty((await _client.GetFromJsonAsync<JsonElement>("/api/discovery/indexer-catalog/sources")).EnumerateArray());
        Assert.Empty((await _client.GetFromJsonAsync<JsonElement>("/api/discovery/indexer-catalog")).EnumerateArray());
    }

    [Fact]
    public async Task Source_catalog_install_test_and_settings_use_the_documented_envelopes()
    {
        _manifests.Serve(SourceUrl, Manifest(("alpha", true), ("beta", false)));

        var created = await _client.PostAsJsonAsync("/api/discovery/indexer-catalog/sources",
            new { name = "Example catalog", url = SourceUrl });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var source = await created.Content.ReadFromJsonAsync<JsonElement>();
        var sourceId = source.GetProperty("id").GetGuid();
        Assert.Equal($"/api/discovery/indexer-catalog/sources/{sourceId}", created.Headers.Location?.OriginalString);
        Assert.Equal("Example catalog", source.GetProperty("name").GetString());
        Assert.Equal(SourceUrl, source.GetProperty("url").GetString());
        Assert.True(source.GetProperty("enabled").GetBoolean());
        Assert.Equal(JsonValueKind.String, source.GetProperty("createdAt").ValueKind);
        Assert.Equal(JsonValueKind.String, source.GetProperty("lastRefreshedAt").ValueKind);
        Assert.True(source.GetProperty("lastRefreshSucceeded").GetBoolean());
        Assert.Equal("discovery.catalog_source.refreshed", source.GetProperty("lastRefreshCode").GetString());
        Assert.Equal("Fetched 2 entries.", source.GetProperty("lastRefreshMessage").GetString());
        Assert.Equal(2, source.GetProperty("entryCount").GetInt32());

        var listed = Assert.Single((await _client.GetFromJsonAsync<JsonElement>("/api/discovery/indexer-catalog/sources"))
            .EnumerateArray());
        Assert.Equal(sourceId, listed.GetProperty("id").GetGuid());

        var entries = (await _client.GetFromJsonAsync<JsonElement>("/api/discovery/indexer-catalog")).EnumerateArray().ToArray();
        Assert.Equal(["alpha", "beta"], entries.Select(item => item.GetProperty("key").GetString()));
        var entry = entries[0];
        Assert.Equal(1, entry.GetProperty("version").GetInt32());
        Assert.Equal("Example alpha", entry.GetProperty("name").GetString());
        Assert.Equal("A neutral test indexer.", entry.GetProperty("description").GetString());
        Assert.Equal("Definition", entry.GetProperty("protocol").GetString());
        Assert.Equal("Torrent", entry.GetProperty("releaseProtocol").GetString());
        Assert.Equal("https://indexer.example/", Assert.Single(entry.GetProperty("baseUrls").EnumerateArray()).GetString());
        Assert.True(entry.GetProperty("requiresFlareSolverr").GetBoolean());
        Assert.Equal(20, entry.GetProperty("defaultPriority").GetInt32());
        Assert.True(entry.GetProperty("defaultSettings").GetProperty("useFlareSolverr").GetBoolean());
        Assert.Equal("Day", entry.GetProperty("defaultSettings").GetProperty("limitsUnit").GetString());
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("installedIndexerId").ValueKind);
        Assert.Equal(sourceId, entry.GetProperty("sourceId").GetGuid());
        Assert.Equal("Example catalog", entry.GetProperty("sourceName").GetString());

        var live = await _client.PostAsJsonAsync($"/api/discovery/indexer-catalog/{sourceId}/alpha/test", new { });
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal("discovery.indexer.transport_unavailable",
            (await live.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        var install = await _client.PostAsJsonAsync($"/api/discovery/indexer-catalog/{sourceId}/alpha/install", new { });
        Assert.Equal(HttpStatusCode.OK, install.StatusCode);
        var indexerId = (await install.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("indexerId").GetGuid();
        var again = await _client.PostAsJsonAsync($"/api/discovery/indexer-catalog/{sourceId}/alpha/install", new { });
        Assert.Equal(indexerId, (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("indexerId").GetGuid());

        var installedCatalog = await _client.GetFromJsonAsync<JsonElement>("/api/discovery/indexer-catalog");
        Assert.Equal(indexerId, installedCatalog.EnumerateArray().First(item => item.GetProperty("key").GetString() == "alpha")
            .GetProperty("installedIndexerId").GetGuid());

        var indexer = Assert.Single((await _client.GetFromJsonAsync<JsonElement>("/api/discovery/indexers")).EnumerateArray());
        Assert.Equal("alpha", indexer.GetProperty("catalogKey").GetString());
        Assert.Equal(1, indexer.GetProperty("catalogVersion").GetInt32());
        Assert.Equal(sourceId, indexer.GetProperty("catalogSourceId").GetGuid());

        var test = await _client.PostAsync($"/api/discovery/indexers/{indexerId}/test", null);
        Assert.Equal(HttpStatusCode.OK, test.StatusCode);
        var testBody = await test.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(testBody.GetProperty("succeeded").GetBoolean());
        Assert.False(testBody.TryGetProperty("url", out _));
        Assert.False(testBody.TryGetProperty("exception", out _));

        await AssertErrorAsync(
            await _client.PutAsJsonAsync($"/api/discovery/indexers/{indexerId}/settings", Settings(minimumSeeders: -1, useFlareSolverr: true)),
            HttpStatusCode.BadRequest, "discovery.invalid_settings");
        await AssertErrorAsync(
            await _client.PutAsJsonAsync($"/api/discovery/indexers/{indexerId}/settings", Settings(minimumSeeders: 1, useFlareSolverr: false)),
            HttpStatusCode.BadRequest, "discovery.catalog.browser_required");
        await AssertErrorAsync(
            await _client.PostAsJsonAsync($"/api/discovery/indexer-catalog/{sourceId}/missing/install", new { }),
            HttpStatusCode.BadRequest, "discovery.catalog.not_found");
        await AssertErrorAsync(
            await _client.PostAsJsonAsync($"/api/discovery/indexer-catalog/{Guid.NewGuid()}/alpha/install", new { }),
            HttpStatusCode.BadRequest, "discovery.catalog_source.not_found");
    }

    [Fact]
    public async Task The_single_key_routes_of_the_built_in_catalog_are_gone()
    {
        var install = await _client.PostAsJsonAsync("/api/discovery/indexer-catalog/alpha/install", new { });
        var test = await _client.PostAsJsonAsync("/api/discovery/indexer-catalog/alpha/test", new { });

        Assert.Equal(HttpStatusCode.NotFound, install.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, test.StatusCode);
    }

    [Fact]
    public async Task A_hostile_manifest_is_recorded_on_the_source_and_installs_nothing()
    {
        const string loginBlock = """
            "session": { "login": { "method": "Post", "urlTemplate": "/login",
              "fields": [ { "name": "u", "valueTemplate": "{{credential.username}}" } ] } },
            """;
        var login = Definition.Replace("\"search\": {", loginBlock + " \"search\": {", StringComparison.Ordinal);
        _manifests.ServeRaw(SourceUrl, $$"""{ "schemaVersion": 1, "entries": [ {{EntryJson("private", false, login)}} ] }""");

        var created = await _client.PostAsJsonAsync("/api/discovery/indexer-catalog/sources", new { name = "Hostile", url = SourceUrl });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var source = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(source.GetProperty("lastRefreshSucceeded").GetBoolean());
        Assert.Equal("discovery.catalog_source.invalid_manifest", source.GetProperty("lastRefreshCode").GetString());
        Assert.Contains("login", source.GetProperty("lastRefreshMessage").GetString(), StringComparison.Ordinal);
        Assert.Equal(0, source.GetProperty("entryCount").GetInt32());
        Assert.Empty((await _client.GetFromJsonAsync<JsonElement>("/api/discovery/indexer-catalog")).EnumerateArray());
    }

    [Fact]
    public async Task Source_errors_use_the_error_envelope_with_their_status()
    {
        _manifests.Serve(SourceUrl, Manifest());
        Assert.Equal(HttpStatusCode.Created,
            (await _client.PostAsJsonAsync("/api/discovery/indexer-catalog/sources", new { name = "One", url = SourceUrl })).StatusCode);
        var unknown = Guid.NewGuid();

        await AssertErrorAsync(
            await _client.PostAsJsonAsync("/api/discovery/indexer-catalog/sources", new { name = "Two", url = SourceUrl }),
            HttpStatusCode.Conflict, "discovery.catalog_source.duplicate_url");
        await AssertErrorAsync(
            await _client.PostAsJsonAsync("/api/discovery/indexer-catalog/sources", new { name = "Private", url = "https://192.168.0.2/x.json" }),
            HttpStatusCode.BadRequest, "discovery.catalog_source.invalid_url");
        await AssertErrorAsync(
            await _client.PostAsJsonAsync("/api/discovery/indexer-catalog/sources", new { name = "Plain", url = "http://catalog.example.org/x.json" }),
            HttpStatusCode.BadRequest, "discovery.catalog_source.invalid_url");
        await AssertErrorAsync(
            await _client.PostAsJsonAsync("/api/discovery/indexer-catalog/sources", new { name = " ", url = "https://other.example.org/x.json" }),
            HttpStatusCode.BadRequest, "discovery.catalog_source.invalid_name");
        await AssertErrorAsync(
            await _client.PostAsync($"/api/discovery/indexer-catalog/sources/{unknown}/refresh", null),
            HttpStatusCode.NotFound, "discovery.catalog_source.not_found");
        await AssertErrorAsync(
            await _client.PutAsJsonAsync($"/api/discovery/indexer-catalog/sources/{unknown}", new { name = "x", enabled = true }),
            HttpStatusCode.NotFound, "discovery.catalog_source.not_found");
        await AssertErrorAsync(
            await _client.DeleteAsync($"/api/discovery/indexer-catalog/sources/{unknown}"),
            HttpStatusCode.NotFound, "discovery.catalog_source.not_found");
    }

    [Fact]
    public async Task Refresh_update_and_delete_keep_installed_indexers()
    {
        _manifests.Serve(SourceUrl, Manifest(("alpha", false)));
        var source = await (await _client.PostAsJsonAsync("/api/discovery/indexer-catalog/sources", new { name = "Example", url = SourceUrl }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var sourceId = source.GetProperty("id").GetGuid();
        var indexerId = (await (await _client.PostAsJsonAsync($"/api/discovery/indexer-catalog/{sourceId}/alpha/install", new { }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("indexerId").GetGuid();

        // The source goes down: the refresh answers 200 with the failure, and the snapshot stays.
        _manifests.Fail(SourceUrl);
        var refresh = await _client.PostAsync($"/api/discovery/indexer-catalog/sources/{sourceId}/refresh", null);
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        var refreshed = await refresh.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(refreshed.GetProperty("lastRefreshSucceeded").GetBoolean());
        Assert.Equal("discovery.catalog_source.fetch_failed", refreshed.GetProperty("lastRefreshCode").GetString());
        Assert.Equal(1, refreshed.GetProperty("entryCount").GetInt32());

        var update = await _client.PutAsJsonAsync($"/api/discovery/indexer-catalog/sources/{sourceId}", new { name = "Renamed", enabled = false });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var updated = await update.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Renamed", updated.GetProperty("name").GetString());
        Assert.False(updated.GetProperty("enabled").GetBoolean());
        Assert.Empty((await _client.GetFromJsonAsync<JsonElement>("/api/discovery/indexer-catalog")).EnumerateArray());

        var delete = await _client.DeleteAsync($"/api/discovery/indexer-catalog/sources/{sourceId}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Empty((await _client.GetFromJsonAsync<JsonElement>("/api/discovery/indexer-catalog/sources")).EnumerateArray());
        var indexer = Assert.Single((await _client.GetFromJsonAsync<JsonElement>("/api/discovery/indexers")).EnumerateArray());
        Assert.Equal(indexerId, indexer.GetProperty("id").GetGuid());
        Assert.True(indexer.GetProperty("enabled").GetBoolean());
        Assert.Equal("alpha", indexer.GetProperty("catalogKey").GetString());
        Assert.Equal(JsonValueKind.Null, indexer.GetProperty("catalogSourceId").ValueKind);
    }

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, body.GetProperty("error").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("message").GetString()));
    }

    private static object Settings(int minimumSeeders, bool useFlareSolverr) => new
    {
        minimumSeeders,
        preferMagnet = true,
        queryLimit = (int?)null,
        grabLimit = (int?)null,
        limitsUnit = "Day",
        useFlareSolverr,
    };

    private static string Manifest(params (string Key, bool RequiresFlareSolverr)[] entries) =>
        $$"""{ "schemaVersion": 1, "name": "Example", "entries": [ {{string.Join(", ", entries.Select(e => EntryJson(e.Key, e.RequiresFlareSolverr, Definition)))}} ] }""";

    private static string EntryJson(string key, bool requiresFlareSolverr, string definition)
    {
        var flag = requiresFlareSolverr ? "true" : "false";
        return $$"""
            {
              "key": "{{key}}", "version": 1, "name": "Example {{key}}", "description": "A neutral test indexer.",
              "protocol": "Definition", "releaseProtocol": "Torrent", "baseUrls": ["https://indexer.example/"],
              "requiresFlareSolverr": {{flag}}, "defaultPriority": 20,
              "defaultSettings": { "minimumSeeders": 1, "preferMagnet": true, "limitsUnit": "Day", "useFlareSolverr": {{flag}} },
              "definition": {{definition}}
            }
            """;
    }

    /// <summary>Canned manifests per URL in place of the network.</summary>
    private sealed class ManifestServer : IIndexerCatalogFetcher
    {
        private readonly ConcurrentDictionary<string, Result<string>> responses = new(StringComparer.Ordinal);

        public void Serve(string url, string body) => ServeRaw(url, body);

        public void ServeRaw(string url, string body) => responses[new Uri(url).ToString()] = Result<string>.Success(body);

        public void Fail(string url) => responses[new Uri(url).ToString()] = Result<string>.Failure(
            new Error("discovery.catalog_source.fetch_failed", "The source answered HTTP 503."));

        public Task<Result<string>> FetchAsync(Uri url, CancellationToken cancellationToken) =>
            Task.FromResult(responses.TryGetValue(url.ToString(), out var response)
                ? response
                : Result<string>.Failure(new Error("discovery.catalog_source.fetch_failed", "Unreachable.")));
    }
}
