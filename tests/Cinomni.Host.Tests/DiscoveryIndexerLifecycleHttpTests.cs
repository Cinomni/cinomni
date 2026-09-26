using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Cinomni.Discovery.Persistence;
using Cinomni.Identity.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Host.Tests;

/// <summary>
/// Drives enable, priority and delete through the mapped routes an operator's browser calls, then
/// reads the listing back. A test that builds the record by hand would not catch a route that
/// accepted the body and never wrote it.
/// </summary>
[Trait("Category", "RequiresDatabase")]
public sealed class DiscoveryIndexerLifecycleHttpTests : IAsyncLifetime
{
    private const string Database = "cinomni_test_discovery_indexer_lifecycle_http";
    private const string AdminUsername = "operator";
    private const string Password = "correct horse battery staple";

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

    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _app = await DiscoveryDefinitionHttpTestHost.StartAsync(Database);
        string token;
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var admin = await scope.ServiceProvider.GetRequiredService<IUserProvisioning>()
                .CreateAdminAsync(AdminUsername, Password);
            Assert.True(admin.IsSuccess, admin.IsFailure ? admin.Error.Message : null);
            token = (await scope.ServiceProvider.GetRequiredService<ISessionService>().IssueAsync(admin.Value)).Token;
        }

        _client = _app.GetTestClient();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task Enabling_and_priority_are_what_the_listing_returns()
    {
        var indexerId = await AddIndexerAsync("Listed", "https://listed.example/api", 25);

        var disabled = await _client.PutAsJsonAsync($"/api/discovery/indexers/{indexerId}/enabled", new { enabled = false });
        Assert.Equal(HttpStatusCode.NoContent, disabled.StatusCode);
        var afterDisable = await FindAsync(indexerId);
        Assert.False(afterDisable.GetProperty("enabled").GetBoolean());

        var reordered = await _client.PutAsJsonAsync($"/api/discovery/indexers/{indexerId}/priority", new { priority = 4 });
        Assert.Equal(HttpStatusCode.NoContent, reordered.StatusCode);
        var afterReorder = await FindAsync(indexerId);
        Assert.Equal(4, afterReorder.GetProperty("priority").GetInt32());
        Assert.False(afterReorder.GetProperty("enabled").GetBoolean());

        var rejected = await _client.PutAsJsonAsync($"/api/discovery/indexers/{indexerId}/priority", new { priority = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        var rejectedBody = await rejected.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("discovery.invalid_priority", rejectedBody.GetProperty("error").GetString());
        Assert.Equal(4, (await FindAsync(indexerId)).GetProperty("priority").GetInt32());
    }

    [Fact]
    public async Task Delete_removes_the_indexer_the_listing_shows_and_keeps_the_definition()
    {
        var definitionId = await UploadDefinitionAsync();
        var removed = await AddIndexerAsync("Removed", "https://removed.example", 1, definitionId);
        var kept = await AddIndexerAsync("Kept", "https://kept.example", 2, definitionId);

        var deleted = await _client.DeleteAsync($"/api/discovery/indexers/{removed}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var listing = await _client.GetFromJsonAsync<JsonElement>("/api/discovery/indexers");
        var ids = listing.EnumerateArray().Select(row => row.GetProperty("id").GetString()).ToList();
        Assert.DoesNotContain(removed, ids);
        Assert.Contains(kept, ids);

        var definitions = await _client.GetFromJsonAsync<JsonElement>("/api/discovery/indexer-definitions");
        Assert.Contains(definitions.EnumerateArray(), row => row.GetProperty("id").GetString() == definitionId);

        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>();
        Assert.False(await db.Indexers.AnyAsync(indexer => indexer.Id == Guid.Parse(removed)));
        Assert.True(await db.IndexerDefinitions.AnyAsync(definition => definition.Id == Guid.Parse(definitionId)));
        Assert.False(await db.IndexerSessions.AnyAsync(session => session.IndexerId == Guid.Parse(removed)));

        var again = await _client.DeleteAsync($"/api/discovery/indexers/{removed}");
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [Fact]
    public async Task An_unknown_indexer_and_an_anonymous_caller_are_refused()
    {
        var missing = Guid.NewGuid();
        var enabled = await _client.PutAsJsonAsync($"/api/discovery/indexers/{missing}/enabled", new { enabled = false });
        Assert.Equal(HttpStatusCode.NotFound, enabled.StatusCode);

        using var anonymous = _app.GetTestClient();
        var refused = await anonymous.PutAsJsonAsync(
            $"/api/discovery/indexers/{missing}/enabled", new { enabled = true });
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
    }

    private async Task<string> AddIndexerAsync(string name, string baseUrl, int priority, string? definitionId = null)
    {
        var response = await _client.PostAsJsonAsync("/api/discovery/indexers", new
        {
            name,
            protocol = definitionId is null ? "Torznab" : "Definition",
            baseUrl,
            priority,
            definitionId,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("indexerId").GetString()!;
    }

    private async Task<string> UploadDefinitionAsync()
    {
        var response = await _client.PostAsJsonAsync("/api/discovery/indexer-definitions", new
        {
            name = "Shared",
            rawContent = ValidDefinition,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("definitionId").GetString()!;
    }

    private async Task<JsonElement> FindAsync(string indexerId)
    {
        var listing = await _client.GetFromJsonAsync<JsonElement>("/api/discovery/indexers");
        return listing.EnumerateArray().Single(row => row.GetProperty("id").GetString() == indexerId);
    }
}
