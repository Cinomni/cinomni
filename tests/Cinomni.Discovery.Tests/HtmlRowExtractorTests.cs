using Cinomni.Discovery.Indexers.Definition;

namespace Cinomni.Discovery.Tests;

/// <summary>
/// Unit tests for HTML row extraction against a fictional test site's markup. AngleSharp's HTML5
/// parser recovers from malformed markup per spec, so the interesting cases here are selector
/// matching, the attribute vocabulary (Text/Href/Src), and the row-count ceiling — never a thrown
/// exception.
/// </summary>
public sealed class HtmlRowExtractorTests
{
    private const string TwoRowHtml = """
        <table class="results">
          <tr class="result">
            <td class="name"><a href="/details/1">Interstellar 2014 1080p BluRay x264</a></td>
            <td class="dl"><a href="/download/1.torrent">DL</a></td>
            <td class="size">1.2 GB</td>
            <td class="seed">120</td>
            <td class="date">2024-11-12</td>
          </tr>
          <tr class="result">
            <td class="name"><a href="/details/2">Interstellar 2014 720p WEB</a></td>
            <td class="dl"><a href="/download/2.torrent">DL</a></td>
          </tr>
        </table>
        """;

    private static readonly DefinitionFields Fields = new(
        Title: new DefinitionFieldRule("td.name a", DefinitionFieldAttribute.Text),
        DownloadUrl: new DefinitionFieldRule("td.dl a", DefinitionFieldAttribute.Href),
        SizeBytes: new DefinitionFieldRule("td.size", DefinitionFieldAttribute.Text, DefinitionFieldTransform.ParseSize),
        Seeders: new DefinitionFieldRule("td.seed", DefinitionFieldAttribute.Text, DefinitionFieldTransform.ParseInt),
        PublishedAt: new DefinitionFieldRule("td.date", DefinitionFieldAttribute.Text, DefinitionFieldTransform.ParseDate, "yyyy-MM-dd"));

    [Fact]
    public void Extracts_every_declared_field_from_a_matched_row()
    {
        var search = new DefinitionSearch(
            [], DefinitionResponseFormat.Html, new DefinitionRowRule("table.results tr.result", 50), Fields);

        var rows = HtmlRowExtractor.ExtractRows(TwoRowHtml, search);

        Assert.Equal(2, rows.Count);
        Assert.Equal("Interstellar 2014 1080p BluRay x264", rows[0].Title);
        Assert.Equal("/download/1.torrent", rows[0].DownloadUrl);
        Assert.Equal("1.2 GB", rows[0].SizeBytes);
        Assert.Equal("120", rows[0].Seeders);
        Assert.Equal("2024-11-12", rows[0].PublishedAt);
    }

    [Fact]
    public void A_row_missing_an_optional_field_reads_null_for_it()
    {
        var search = new DefinitionSearch(
            [], DefinitionResponseFormat.Html, new DefinitionRowRule("table.results tr.result", 50), Fields);

        var rows = HtmlRowExtractor.ExtractRows(TwoRowHtml, search);

        Assert.Null(rows[1].SizeBytes);
        Assert.Null(rows[1].Seeders);
        Assert.Null(rows[1].PublishedAt);
    }

    [Fact]
    public void Stops_at_max_rows_even_when_more_rows_match()
    {
        var search = new DefinitionSearch(
            [], DefinitionResponseFormat.Html, new DefinitionRowRule("table.results tr.result", 1), Fields);

        var rows = HtmlRowExtractor.ExtractRows(TwoRowHtml, search);

        Assert.Single(rows);
    }

    [Fact]
    public void Returns_no_rows_when_the_selector_matches_nothing()
    {
        var search = new DefinitionSearch(
            [], DefinitionResponseFormat.Html, new DefinitionRowRule("table.nonexistent tr", 50), Fields);

        var rows = HtmlRowExtractor.ExtractRows(TwoRowHtml, search);

        Assert.Empty(rows);
    }

    [Fact]
    public void Recovers_from_malformed_markup_without_throwing()
    {
        const string broken = """
            <table class="results"><tr class="result"><td class="name"><a href="/d/1">Broken
            <tr class="result"><td class="name"><a href="/d/2">Also broken</a>
            """;
        var search = new DefinitionSearch(
            [], DefinitionResponseFormat.Html, new DefinitionRowRule("table.results tr.result", 50), Fields);

        var rows = HtmlRowExtractor.ExtractRows(broken, search);

        // HTML5 error recovery still yields a document; the exact shape doesn't matter here, only
        // that this never throws.
        Assert.True(rows.Count >= 0);
    }
}
