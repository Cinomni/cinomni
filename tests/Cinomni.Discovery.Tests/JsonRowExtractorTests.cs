using Cinomni.Discovery.Indexers.Definition;

namespace Cinomni.Discovery.Tests;

/// <summary>
/// Unit tests for JSON row extraction via the hand-rolled dot-path evaluator, against a fictional
/// test site's response shape. Malformed JSON or a rows path that isn't an array must degrade to
/// zero rows, never a thrown exception.
/// </summary>
public sealed class JsonRowExtractorTests
{
    private const string TwoRowJson = """
        {
          "data": {
            "rows": [
              { "title": "Interstellar 2014 1080p BluRay x264", "url": "/download/1.torrent", "size": "1.2 GB", "seeders": 120, "date": "2024-11-12" },
              { "title": "Interstellar 2014 720p WEB", "url": "/download/2.torrent" }
            ]
          }
        }
        """;

    private static readonly DefinitionFields Fields = new(
        Title: new DefinitionFieldRule("title", DefinitionFieldAttribute.Text),
        DownloadUrl: new DefinitionFieldRule("url", DefinitionFieldAttribute.Text),
        SizeBytes: new DefinitionFieldRule("size", DefinitionFieldAttribute.Text, DefinitionFieldTransform.ParseSize),
        Seeders: new DefinitionFieldRule("seeders", DefinitionFieldAttribute.Text, DefinitionFieldTransform.ParseInt),
        PublishedAt: new DefinitionFieldRule("date", DefinitionFieldAttribute.Text, DefinitionFieldTransform.ParseDate, "yyyy-MM-dd"));

    private static DefinitionSearch Search(int maxRows = 50) =>
        new([], DefinitionResponseFormat.Json, new DefinitionRowRule("data.rows", maxRows), Fields);

    [Fact]
    public void Extracts_every_declared_field_including_a_numeric_json_value()
    {
        var rows = JsonRowExtractor.ExtractRows(TwoRowJson, Search());

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
        var rows = JsonRowExtractor.ExtractRows(TwoRowJson, Search());

        Assert.Null(rows[1].SizeBytes);
        Assert.Null(rows[1].Seeders);
        Assert.Null(rows[1].PublishedAt);
    }

    [Fact]
    public void Stops_at_max_rows_even_when_more_rows_exist()
    {
        var rows = JsonRowExtractor.ExtractRows(TwoRowJson, Search(maxRows: 1));

        Assert.Single(rows);
    }

    [Fact]
    public void Returns_no_rows_when_malformed()
    {
        var rows = JsonRowExtractor.ExtractRows("this is not { json", Search());

        Assert.Empty(rows);
    }

    [Fact]
    public void Returns_no_rows_when_the_path_does_not_resolve_to_an_array()
    {
        var rows = JsonRowExtractor.ExtractRows("""{ "data": { "rows": "not an array" } }""", Search());

        Assert.Empty(rows);
    }

    [Fact]
    public void Returns_no_rows_when_the_path_segment_is_missing()
    {
        var rows = JsonRowExtractor.ExtractRows("""{ "other": {} }""", Search());

        Assert.Empty(rows);
    }
}
