using System.Collections.Concurrent;
using System.Text.Json;
using Cinomni.Discovery.Application;
using Cinomni.Kernel.Results;

namespace Cinomni.Discovery.Tests;

/// <summary>
/// Serves canned manifest bodies per URL instead of the network, and counts the fetches. A URL with
/// nothing registered answers the same named failure a host that cannot be reached would.
/// </summary>
internal sealed class FakeCatalogFetcher : IIndexerCatalogFetcher
{
    private readonly ConcurrentDictionary<string, Result<string>> responses = new(StringComparer.Ordinal);

    public ConcurrentQueue<Uri> Requests { get; } = new();

    public void Serve(string url, string body) => responses[new Uri(url).ToString()] = Result<string>.Success(body);

    public void Fail(string url, string code, string message) =>
        responses[new Uri(url).ToString()] = Result<string>.Failure(new Error(code, message));

    public Task<Result<string>> FetchAsync(Uri url, CancellationToken cancellationToken)
    {
        Requests.Enqueue(url);
        return Task.FromResult(responses.TryGetValue(url.ToString(), out var response)
            ? response
            : Result<string>.Failure(new Error(HttpIndexerCatalogFetcher.FetchFailedCode, "The source could not be reached.")));
    }
}

/// <summary>
/// Builds catalog manifests for tests. Every host is a reserved example domain: no real indexer site
/// appears in a fixture, and none may.
/// </summary>
internal static class CatalogManifests
{
    public const string SourceUrl = "https://catalog.example.org/cinomni/indexers.json";

    public const string OtherSourceUrl = "https://mirror.example.org/cinomni/indexers.json";

    public const string Definition = """
        {
          "schemaVersion": 1,
          "resultKind": "Torrent",
          "search": {
            "requests": [
              { "contentKinds": ["Movie", "Series", "Season", "Episode"], "method": "Get", "urlTemplate": "/search?q={{term}}" }
            ],
            "responseFormat": "Html",
            "rows": { "selector": "table.results tbody tr", "maxRows": 100 },
            "fields": {
              "title": { "selector": "td.name a", "attribute": "Text", "transform": "Trim" },
              "downloadUrl": { "selector": "a[href^='magnet:']", "attribute": "Href", "transform": "Trim" },
              "seeders": { "selector": "td.seeds", "attribute": "Text", "transform": "ParseInt" }
            }
          }
        }
        """;

    public const string DefinitionWithLogin = """
        {
          "schemaVersion": 1,
          "resultKind": "Torrent",
          "session": {
            "login": {
              "method": "Post",
              "urlTemplate": "/login",
              "fields": [
                { "name": "username", "valueTemplate": "{{credential.username}}" },
                { "name": "password", "valueTemplate": "{{credential.password}}" }
              ]
            }
          },
          "search": {
            "requests": [
              { "contentKinds": ["Movie"], "method": "Get", "urlTemplate": "/search?q={{term}}" }
            ],
            "responseFormat": "Html",
            "rows": { "selector": "tr", "maxRows": 10 },
            "fields": {
              "title": { "selector": "td a", "attribute": "Text" },
              "downloadUrl": { "selector": "td a", "attribute": "Href" }
            }
          }
        }
        """;

    /// <summary>One entry as a JSON object; override any member by passing it.</summary>
    public static Dictionary<string, object?> Entry(
        string key,
        string baseUrl = "https://indexer.example/",
        bool requiresFlareSolverr = false,
        int version = 1,
        string? definition = null) => new()
        {
            ["key"] = key,
            ["version"] = version,
            ["name"] = $"Example {key}",
            ["description"] = "A neutral test indexer.",
            ["protocol"] = "Definition",
            ["releaseProtocol"] = "Torrent",
            ["baseUrls"] = new[] { baseUrl },
            ["requiresFlareSolverr"] = requiresFlareSolverr,
            ["defaultPriority"] = 20,
            ["defaultSettings"] = new Dictionary<string, object?>
            {
                ["minimumSeeders"] = 1,
                ["preferMagnet"] = true,
                ["limitsUnit"] = "Day",
                ["useFlareSolverr"] = requiresFlareSolverr,
            },
            ["definition"] = JsonDocument.Parse(definition ?? Definition).RootElement.Clone(),
        };

    public static string Manifest(params Dictionary<string, object?>[] entries) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1,
            ["name"] = "Example catalog",
            ["entries"] = entries,
        });
}
