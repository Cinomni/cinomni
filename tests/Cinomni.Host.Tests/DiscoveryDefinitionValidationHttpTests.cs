using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Cinomni.Identity.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Host.Tests;

/// <summary>
/// Drives the definition dry run over HTTP, as a real signed-in administrator, against a real
/// PostgreSQL database.
/// <para>
/// This is the layer the defect was visible at and the layer no test reached. The dry run exists so
/// an operator can debug a definition against a saved page, and it reported <c>sizeBytes: 0</c> for
/// every row of a site whose size column reads "473K" — without ever saying that the size rule had
/// failed. A working rule and a broken one produced the same body, so the one affordance built for
/// debugging a definition could not distinguish them. Only a real request through the mapped route,
/// read as JSON, can show what the operator is actually shown.
/// </para>
/// <para>Its own database, like every other PostgreSQL-backed suite here.</para>
/// </summary>
[Trait("Category", "RequiresDatabase")]
public sealed class DiscoveryDefinitionValidationHttpTests : IAsyncLifetime
{
    private const string Database = "cinomni_test_discovery_definition_validate_http";
    private const string AdminUsername = "operator";
    private const string Password = "correct horse battery staple";

    /// <summary>Declares a size rule, so a row that fails it is a rule failure and not an absent field.</summary>
    private const string DefinitionWithSize = """
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

    /// <summary>Declares no size rule at all, which is a different thing from a rule that failed.</summary>
    private const string DefinitionWithoutSize = """
        {
          "schemaVersion": 1,
          "resultKind": "Torrent",
          "search": {
            "requests": [{ "contentKinds": ["Movie"], "method": "Get", "urlTemplate": "https://idx.example/search?q={{term}}" }],
            "responseFormat": "Html",
            "rows": { "selector": "tr.result", "maxRows": 50 },
            "fields": {
              "title": { "selector": "td.name a", "attribute": "Text" },
              "downloadUrl": { "selector": "td.dl a", "attribute": "Href", "transform": "ResolveRelativeUrl" }
            }
          }
        }
        """;

    /// <summary>
    /// Row 0 carries the directory-listing suffix a real site served; row 1 carries a size the rule
    /// cannot read at all.
    /// </summary>
    private const string SampleResponse = """
        <table>
          <tr class="result">
            <td class="name"><a href="/details/1">Interstellar 2014 1080p BluRay x264</a></td>
            <td class="dl"><a href="/download/1.torrent">DL</a></td>
            <td class="size">473K</td>
          </tr>
          <tr class="result">
            <td class="name"><a href="/details/2">Interstellar 2014 720p WEB</a></td>
            <td class="dl"><a href="/download/2.torrent">DL</a></td>
            <td class="size">n/a</td>
          </tr>
        </table>
        """;

    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _app = await DiscoveryDefinitionHttpTestHost.StartAsync(Database);
        _client = _app.GetTestClient();

        await using var scope = _app.Services.CreateAsyncScope();
        var admin = await scope.ServiceProvider.GetRequiredService<IUserProvisioning>()
            .CreateAdminAsync(AdminUsername, Password);
        Assert.True(admin.IsSuccess, admin.IsFailure ? admin.Error.Message : null);
        var token = (await scope.ServiceProvider.GetRequiredService<ISessionService>().IssueAsync(admin.Value)).Token;
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task Reads_a_directory_listing_size_suffix_instead_of_reporting_zero_bytes()
    {
        var body = await ValidateAsync();

        var candidates = body.RootElement.GetProperty("candidates").EnumerateArray().ToList();
        Assert.Equal(2, candidates.Count);
        Assert.Equal(484352L, candidates[0].GetProperty("sizeBytes").GetInt64());
    }

    [Fact]
    public async Task Reports_the_field_rule_that_failed_instead_of_silently_reporting_zero_bytes()
    {
        var body = await ValidateAsync();

        var issues = body.RootElement.GetProperty("fieldIssues").EnumerateArray().ToList();
        var issue = Assert.Single(issues);
        Assert.Equal(1, issue.GetProperty("rowIndex").GetInt32());
        Assert.Equal("sizeBytes", issue.GetProperty("field").GetString());
        Assert.Equal("discovery.definition.transform.parse_size_failed", issue.GetProperty("code").GetString());
        Assert.Contains("n/a", issue.GetProperty("message").GetString());
        Assert.Equal("n/a", issue.GetProperty("rawValue").GetString());

        // The row is still a candidate: the search path degrades rather than dropping a release, and
        // the dry run has to show exactly what the search path would produce.
        var candidates = body.RootElement.GetProperty("candidates").EnumerateArray().ToList();
        Assert.Equal(0L, candidates[1].GetProperty("sizeBytes").GetInt64());
    }

    [Fact]
    public async Task Reports_no_issue_for_a_definition_that_declares_no_size_rule()
    {
        var body = await ValidateAsync(DefinitionWithoutSize);

        Assert.Empty(body.RootElement.GetProperty("fieldIssues").EnumerateArray());
        var candidates = body.RootElement.GetProperty("candidates").EnumerateArray().ToList();
        Assert.Equal(2, candidates.Count);
        Assert.Equal(0L, candidates[0].GetProperty("sizeBytes").GetInt64());
    }

    [Fact]
    public async Task Requires_an_administrator()
    {
        using var anonymous = _app.GetTestClient();

        using var response = await anonymous.PostAsJsonAsync(
            "/api/discovery/indexer-definitions/validate",
            new { rawContent = DefinitionWithSize });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private async Task<JsonDocument> ValidateAsync(string? rawContent = null)
    {
        using var response = await _client.PostAsJsonAsync(
            "/api/discovery/indexer-definitions/validate",
            new
            {
                rawContent = rawContent ?? DefinitionWithSize,
                sampleResponseBody = SampleResponse,
                sampleRequestUrl = "https://idx.example/search?q=Interstellar",
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }
}
