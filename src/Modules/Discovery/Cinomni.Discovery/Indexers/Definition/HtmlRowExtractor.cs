using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace Cinomni.Discovery.Indexers.Definition;

/// <summary>
/// Extracts result rows from an HTML response using the definition's CSS selectors. Pure and
/// side-effect free: no network, no filesystem. AngleSharp's HTML5 parser recovers from malformed
/// markup per spec rather than throwing, so a hostile/broken response degrades to "fewer or no
/// rows matched", never an exception.
/// <para>
/// Bounded by <see cref="DefinitionRowRule.MaxRows"/> at the row level; the response body itself
/// is bounded upstream by the transport's response-size cap, not here.
/// </para>
/// </summary>
internal static class HtmlRowExtractor
{
    public static IReadOnlyList<ExtractedRow> ExtractRows(string html, DefinitionSearch search)
    {
        // One parser per call, as the detail and session parsers already do: searches run concurrently,
        // and AngleSharp does not promise that a parser is safe to share between threads.
        var document = new HtmlParser().ParseDocument(html);

        var rows = new List<ExtractedRow>();
        foreach (var element in document.QuerySelectorAll(search.Rows.Selector))
        {
            if (rows.Count >= search.Rows.MaxRows)
            {
                break;
            }

            rows.Add(ExtractRow(element, search.Fields));
        }

        return rows;
    }

    private static ExtractedRow ExtractRow(IElement row, DefinitionFields fields) => new(
        ReadField(row, fields.Title),
        ReadField(row, fields.DownloadUrl),
        ReadField(row, fields.SizeBytes),
        ReadField(row, fields.Seeders),
        ReadField(row, fields.PublishedAt),
        ReadField(row, fields.Leechers));

    private static string? ReadField(IElement row, DefinitionFieldRule? rule)
    {
        if (rule is null)
        {
            return null;
        }

        var matched = row.QuerySelector(rule.Selector);
        if (matched is null)
        {
            return null;
        }

        return DefinitionElementValue.Read(matched, rule.Attribute);
    }
}
