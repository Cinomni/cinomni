using Cinomni.Discovery.Contracts;
using Cinomni.Discovery.Indexers.Definition;

namespace Cinomni.Discovery.Tests;

/// <summary>
/// Unit tests for the definition response parser end to end: row extraction plus the closed
/// transform vocabulary must produce the exact same <see cref="ReleaseCandidate"/> shape
/// <see cref="TorznabFeedParserTests"/> pins for the Torznab path, against a fictional test site.
/// </summary>
public sealed class DefinitionResponseParserTests
{
    private static readonly Uri RequestUri = new("https://idx.example/search?q=Interstellar");

    private static readonly DefinitionFields HtmlFields = new(
        Title: new DefinitionFieldRule("td.name a", DefinitionFieldAttribute.Text),
        DownloadUrl: new DefinitionFieldRule("td.dl a", DefinitionFieldAttribute.Href, DefinitionFieldTransform.ResolveRelativeUrl),
        SizeBytes: new DefinitionFieldRule("td.size", DefinitionFieldAttribute.Text, DefinitionFieldTransform.ParseSize),
        Seeders: new DefinitionFieldRule("td.seed", DefinitionFieldAttribute.Text, DefinitionFieldTransform.ParseInt),
        PublishedAt: new DefinitionFieldRule("td.date", DefinitionFieldAttribute.Text, DefinitionFieldTransform.ParseDate, "yyyy-MM-dd"));

    private static IndexerDefinitionDocument HtmlDocument(ReleaseProtocol protocol = ReleaseProtocol.Torrent) => new(
        1, protocol,
        new DefinitionSearch([], DefinitionResponseFormat.Html, new DefinitionRowRule("table.results tr.result", 50), HtmlFields));

    [Fact]
    public void Parses_titles_urls_size_seeders_date_and_protocol()
    {
        const string html = """
            <table class="results">
              <tr class="result">
                <td class="name"><a href="/details/1">Interstellar 2014 1080p BluRay x264</a></td>
                <td class="dl"><a href="/download/1.torrent">DL</a></td>
                <td class="size">1.2 GB</td>
                <td class="seed">120</td>
                <td class="date">2024-11-12</td>
              </tr>
            </table>
            """;

        var candidates = DefinitionResponseParser.Parse(HtmlDocument(), html, "TestIndexer", RequestUri).Candidates;

        Assert.Single(candidates);
        var candidate = candidates[0];
        Assert.Equal("Interstellar 2014 1080p BluRay x264", candidate.Title);
        Assert.Equal("https://idx.example/download/1.torrent", candidate.DownloadUrl);
        Assert.StartsWith("definition:", candidate.Guid, StringComparison.Ordinal);
        Assert.Equal(75, candidate.Guid.Length);
        Assert.Equal(ReleaseProtocol.Torrent, candidate.Protocol);
        Assert.Equal(1288490189L, candidate.SizeBytes);
        Assert.Equal(120, candidate.Seeders);
        Assert.NotNull(candidate.PublishedAt);
        Assert.Equal("TestIndexer", candidate.IndexerName);
    }

    [Fact]
    public void Long_magnet_link_keeps_its_full_url_but_uses_a_bounded_stable_guid()
    {
        var magnet = $"magnet:?xt=urn:btih:ABC123&tr={new string('x', 1000)}";
        var html = $"""
            <table class="results">
              <tr class="result">
                <td class="name"><a>Big Buck Bunny 2008 720p</a></td>
                <td class="dl"><a href="{magnet}">DL</a></td>
              </tr>
            </table>
            """;

        var first = Assert.Single(
            DefinitionResponseParser.Parse(HtmlDocument(), html, "TestIndexer", RequestUri).Candidates);
        var second = Assert.Single(
            DefinitionResponseParser.Parse(HtmlDocument(), html, "TestIndexer", RequestUri).Candidates);

        Assert.Equal(magnet, first.DownloadUrl);
        Assert.Equal(first.Guid, second.Guid);
        Assert.Equal(75, first.Guid.Length);
    }

    [Fact]
    public void A_row_with_no_size_seeders_or_date_still_becomes_a_candidate_with_zero_and_nulls()
    {
        const string html = """
            <table class="results">
              <tr class="result">
                <td class="name"><a href="/details/2">Interstellar 2014 720p WEB</a></td>
                <td class="dl"><a href="/download/2.torrent">DL</a></td>
              </tr>
            </table>
            """;

        var extraction = DefinitionResponseParser.Parse(HtmlDocument(), html, "TestIndexer", RequestUri);

        var candidate = Assert.Single(extraction.Candidates);
        Assert.Equal(0L, candidate.SizeBytes);
        Assert.Null(candidate.Seeders);
        Assert.Null(candidate.PublishedAt);

        // The rules were declared and matched nothing, which is a different edit from a rule whose
        // transform failed — so it is reported under its own code, naming the selector to fix.
        Assert.Equal(3, extraction.FieldIssues.Count);
        Assert.All(extraction.FieldIssues, issue =>
        {
            Assert.Equal("discovery.definition.field.no_value", issue.Code);
            Assert.Null(issue.RawValue);
            Assert.Equal(0, issue.RowIndex);
        });
        Assert.Equal(
            ["publishedAt", "seeders", "sizeBytes"],
            extraction.FieldIssues.Select(issue => issue.Field).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// The defect: the size transform failed, collapsed to 0L, and nothing anywhere said so — a
    /// release that reads "0 B" was indistinguishable from one the site really declares as empty.
    /// </summary>
    [Fact]
    public void A_size_the_transform_cannot_read_is_reported_and_not_silently_zero()
    {
        const string html = """
            <table class="results">
              <tr class="result">
                <td class="name"><a href="/details/6">Interstellar 2014 2160p</a></td>
                <td class="dl"><a href="/download/6.torrent">DL</a></td>
                <td class="size">a lot</td>
              </tr>
            </table>
            """;

        var extraction = DefinitionResponseParser.Parse(HtmlDocument(), html, "TestIndexer", RequestUri);

        // Still a candidate, still zero: the search path degrades rather than dropping the release.
        var candidate = Assert.Single(extraction.Candidates);
        Assert.Equal(0L, candidate.SizeBytes);

        var issue = Assert.Single(extraction.FieldIssues, i => i.Field == "sizeBytes");
        Assert.Equal(0, issue.RowIndex);
        Assert.Equal("discovery.definition.transform.parse_size_failed", issue.Code);
        Assert.Equal("a lot", issue.RawValue);
        Assert.Contains("a lot", issue.Message);
    }

    /// <summary>
    /// A directory-listing suffix is the size notation a real site served, and it made every one of
    /// its releases report zero bytes. It must extract, and it must raise no issue.
    /// </summary>
    [Fact]
    public void A_directory_listing_size_suffix_extracts_without_an_issue()
    {
        const string html = """
            <table class="results">
              <tr class="result">
                <td class="name"><a href="/details/7">Interstellar 2014 480p</a></td>
                <td class="dl"><a href="/download/7.torrent">DL</a></td>
                <td class="size">473K</td>
              </tr>
            </table>
            """;

        var extraction = DefinitionResponseParser.Parse(HtmlDocument(), html, "TestIndexer", RequestUri);

        Assert.Equal(484352L, Assert.Single(extraction.Candidates).SizeBytes);
        Assert.DoesNotContain(extraction.FieldIssues, issue => issue.Field == "sizeBytes");
    }

    [Fact]
    public void A_declared_rule_that_cannot_produce_its_field_is_reported_rather_than_ignored()
    {
        var fields = new DefinitionFields(
            Title: new DefinitionFieldRule("td.name a", DefinitionFieldAttribute.Text),
            DownloadUrl: new DefinitionFieldRule("td.dl a", DefinitionFieldAttribute.Href, DefinitionFieldTransform.ResolveRelativeUrl),
            // Declared, matching, and carrying no transform that could ever produce a byte count.
            SizeBytes: new DefinitionFieldRule("td.size", DefinitionFieldAttribute.Text));
        var document = new IndexerDefinitionDocument(
            1, ReleaseProtocol.Torrent,
            new DefinitionSearch([], DefinitionResponseFormat.Html, new DefinitionRowRule("tr.result", 50), fields));
        const string html = """
            <table class="results">
              <tr class="result">
                <td class="name"><a href="/details/8">Interstellar 2014 1080p</a></td>
                <td class="dl"><a href="/download/8.torrent">DL</a></td>
                <td class="size">1.2 GB</td>
              </tr>
            </table>
            """;

        var extraction = DefinitionResponseParser.Parse(document, html, "TestIndexer", RequestUri);

        Assert.Equal(0L, Assert.Single(extraction.Candidates).SizeBytes);
        var issue = Assert.Single(extraction.FieldIssues);
        Assert.Equal("sizeBytes", issue.Field);
        Assert.Equal("discovery.definition.field.unsupported_transform", issue.Code);
    }


    [Fact]
    public void Skips_a_row_without_a_title_and_says_so()
    {
        const string html = """
            <table class="results">
              <tr class="result">
                <td class="dl"><a href="/download/3.torrent">DL</a></td>
              </tr>
            </table>
            """;

        var extraction = DefinitionResponseParser.Parse(HtmlDocument(), html, "TestIndexer", RequestUri);

        Assert.Empty(extraction.Candidates);
        var issue = Assert.Single(extraction.FieldIssues);
        Assert.Equal("title", issue.Field);
        Assert.Equal("discovery.definition.field.no_value", issue.Code);
        Assert.Contains("td.name a", issue.Message);
    }

    [Fact]
    public void Skips_a_row_whose_download_url_cannot_be_resolved_and_says_so()
    {
        const string html = """
            <table class="results">
              <tr class="result">
                <td class="name"><a href="/details/4">No download link</a></td>
              </tr>
            </table>
            """;

        var extraction = DefinitionResponseParser.Parse(HtmlDocument(), html, "TestIndexer", RequestUri);

        Assert.Empty(extraction.Candidates);
        var issue = Assert.Single(extraction.FieldIssues);
        Assert.Equal("downloadUrl", issue.Field);
        Assert.Equal("discovery.definition.field.no_value", issue.Code);
    }

    [Fact]
    public void Carries_the_definitions_result_kind_as_the_candidate_protocol()
    {
        const string html = """
            <table class="results">
              <tr class="result">
                <td class="name"><a href="/details/5">A Usenet release</a></td>
                <td class="dl"><a href="/download/5.nzb">DL</a></td>
              </tr>
            </table>
            """;

        var candidates = DefinitionResponseParser
            .Parse(HtmlDocument(ReleaseProtocol.Usenet), html, "TestIndexer", RequestUri).Candidates;

        Assert.Single(candidates);
        Assert.Equal(ReleaseProtocol.Usenet, candidates[0].Protocol);
    }

    [Fact]
    public void Parses_a_json_response_the_same_way()
    {
        var fields = new DefinitionFields(
            Title: new DefinitionFieldRule("title", DefinitionFieldAttribute.Text),
            DownloadUrl: new DefinitionFieldRule("url", DefinitionFieldAttribute.Text),
            SizeBytes: new DefinitionFieldRule("size", DefinitionFieldAttribute.Text, DefinitionFieldTransform.ParseSize));
        var document = new IndexerDefinitionDocument(
            1, ReleaseProtocol.Torrent,
            new DefinitionSearch([], DefinitionResponseFormat.Json, new DefinitionRowRule("data.rows", 50), fields));
        const string json = """
            { "data": { "rows": [
              { "title": "Interstellar 2014 1080p BluRay x264", "url": "https://idx.example/download/1.torrent", "size": "700MB" }
            ] } }
            """;

        var candidates = DefinitionResponseParser.Parse(document, json, "TestIndexer", RequestUri).Candidates;

        Assert.Single(candidates);
        Assert.Equal("https://idx.example/download/1.torrent", candidates[0].DownloadUrl);
        Assert.Equal(734003200L, candidates[0].SizeBytes);
    }
}
