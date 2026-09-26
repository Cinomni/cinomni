using Cinomni.Discovery.Indexers.Definition;
using Cinomni.Search.Contracts;

namespace Cinomni.Discovery.Tests;

/// <summary>
/// A definition of the kind an operator uploads for a private tracker behind a browser challenge,
/// against an invented page structure with invented releases on a reserved example host. The page
/// puts a fixed "recommended" box of eight-column rows above the results, which the row selector
/// must skip.
/// </summary>
public sealed class NestedTableLoginDefinitionTests
{
    internal const string DefinitionJson = """
        {
          "schemaVersion": 1,
          "resultKind": "Torrent",
          "search": {
            "requests": [
              {
                "contentKinds": ["Movie", "Series", "Season", "Episode"],
                "method": "Get",
                "urlTemplate": "/index.php?page=torrents&search={{term}}&active=1&options=0"
              }
            ],
            "responseFormat": "Html",
            "rows": {
              "selector": "table.lista > tbody > tr:has(> td:nth-child(10)):has(a[href*='download.php'])",
              "maxRows": 100
            },
            "fields": {
              "title": { "selector": "td:nth-child(2) a[href*='torrent-details']", "attribute": "Text", "transform": "Trim" },
              "downloadUrl": { "selector": "td:nth-child(4) a[href*='download.php']", "attribute": "Href", "transform": "ResolveRelativeUrl" },
              "sizeBytes": { "selector": "td:nth-child(6)", "attribute": "Text", "transform": "ParseSize" },
              "seeders": { "selector": "td:nth-child(8)", "attribute": "Text", "transform": "ParseInt" }
            }
          },
          "session": {
            "login": {
              "method": "Post",
              "urlTemplate": "/index.php?page=login",
              "fields": [
                { "name": "uid", "valueTemplate": "{{credential.username}}" },
                { "name": "pwd", "valueTemplate": "{{credential.password}}" }
              ]
            },
            "check": { "selector": "input[name='pwd']" }
          }
        }
        """;

    private const string SearchPage = """
        <html><body>
        <table><tbody><tr><td>
          <table class="lista"><tbody>
            <tr><td class="header">Cat.</td><td class="header">Filename</td><td class="header">DL</td></tr>
            <tr>
              <td class="lista"><a href="index.php?page=torrents&amp;category=1"><img src="c.png"></a></td>
              <td class="lista"><a href="index.php?page=torrent-details&amp;id=900">Recommended Film 1999 2160p</a></td>
              <td class="lista"><a href="download.php?id=900&amp;f=rec.torrent"><img src="dl.png"></a></td>
              <td class="lista">10:00:00 01/01/2026</td><td class="lista">80.00 GB</td><td class="lista">someone</td>
              <td class="#FFFF00">50</td><td class="#FF0000">0</td>
            </tr>
          </tbody></table>
        </td></tr></tbody></table>
        <table class="lista"><tbody>
          <tr><td>header</td></tr>
          <tr><td>filters</td></tr>
          <tr><td>
            <table class="lista"><tbody>
              <tr><td class="header">Cat.</td><td class="header">Name</td><td class="header">Com.</td><td class="header">Dl</td>
                  <td class="header">AddDate</td><td class="header">Size</td><td class="header">Uploader</td>
                  <td class="header">S</td><td class="header">L</td><td class="header">C</td></tr>
              <tr>
                <td class="lista"><a href="index.php?page=torrents&amp;category=2"><img src="c.png"></a></td>
                <td class="lista"><a href="index.php?page=torrent-details&amp;id=42">Example Film 2008 720p BluRay x264</a><br><span>Animation</span></td>
                <td class="lista"><a href="index.php?page=torrent-details&amp;id=42#comments">3</a></td>
                <td class="lista"><a href="download.php?id=42&amp;f=Example.torrent"><img src="dl.png"></a></td>
                <td class="lista">December 04, 2009, 21:44:28</td>
                <td class="lista222">364.84 MB</td>
                <td class="lista222"><a href="index.php?page=userdetails&amp;id=7"><span>uploader</span></a></td>
                <td class="#FFFF00"><span class="seedy"><a href="index.php?page=peers&amp;id=42">4</a></span></td>
                <td class="#FF0000"><a href="index.php?page=peers&amp;id=42">0</a></td>
                <td class="lista">12</td>
              </tr>
            </tbody></table>
          </td></tr>
        </tbody></table>
        </body></html>
        """;

    [Fact]
    public void The_definition_parses_strictly_and_declares_a_login_with_a_logged_out_check()
    {
        var parsed = IndexerDefinitionParser.Parse(DefinitionJson, strictSelectors: true);

        Assert.True(parsed.IsSuccess, parsed.IsFailure ? parsed.Error.Message : null);
        Assert.NotNull(parsed.Value.Session);
        Assert.NotNull(parsed.Value.Session!.Check);
    }

    [Fact]
    public void A_search_page_yields_only_the_results_never_the_recommended_box()
    {
        var definition = IndexerDefinitionParser.Parse(DefinitionJson, strictSelectors: true).Value;
        var requestUrl = new Uri("https://tracker.example/index.php?page=torrents&search=Example&active=1&options=0");

        var extraction = DefinitionResponseParser.Parse(definition, SearchPage, "Example Tracker", requestUrl);

        var release = Assert.Single(extraction.Candidates);
        Assert.Equal("Example Film 2008 720p BluRay x264", release.Title);
        Assert.Equal("https://tracker.example/download.php?id=42&f=Example.torrent", release.DownloadUrl);
        Assert.Equal(4, release.Seeders);
        Assert.InRange(release.SizeBytes, 380_000_000, 385_000_000);
        Assert.Empty(extraction.FieldIssues);
    }

    [Fact]
    public void A_search_builds_the_documented_query()
    {
        var definition = IndexerDefinitionParser.Parse(DefinitionJson, strictSelectors: true).Value;

        var query = DefinitionQueryBuilder.Build(
            definition, new SearchCriterion("Example Film", 2008, null, null, "Movie"), new Uri("https://tracker.example/"));

        Assert.True(query.IsSuccess);
        Assert.StartsWith("https://tracker.example/index.php?page=torrents&search=Example", query.Value.Url.ToString());
    }

    [Fact]
    public void A_logged_out_page_is_recognised_by_the_password_field()
    {
        var check = IndexerDefinitionParser.Parse(DefinitionJson).Value.Session!.Check!;

        Assert.True(DefinitionSessionMatcher.IsLoggedOut(
            "<form name='login'><input name='uid'><input type='password' name='pwd'></form>", check.Selector));
        Assert.False(DefinitionSessionMatcher.IsLoggedOut(SearchPage, check.Selector));
    }
}
