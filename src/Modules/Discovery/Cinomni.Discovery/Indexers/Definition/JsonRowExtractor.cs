using System.Text.Json;

namespace Cinomni.Discovery.Indexers.Definition;

/// <summary>
/// Extracts result rows from a JSON response using a small, hand-rolled dot-path evaluator — not a
/// general JSONPath library. The grammar is deliberately closed (a dotted chain of object property
/// names, nothing else) so what a definition can ask for stays auditable. Pure and side-effect
/// free; malformed JSON degrades to zero rows rather than throwing.
/// <para>
/// A field rule's <see cref="DefinitionFieldRule.Attribute"/> has no meaning here (it only
/// distinguishes an HTML element's text from its <c>href</c>/<c>src</c>) — a JSON path already
/// names the exact value, so every field reads the node at its path directly.
/// </para>
/// </summary>
internal static class JsonRowExtractor
{
    public static IReadOnlyList<ExtractedRow> ExtractRows(string json, DefinitionSearch search)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return [];
        }

        using (document)
        {
            var rowsNode = NavigatePath(document.RootElement, search.Rows.Selector);
            if (rowsNode is not { ValueKind: JsonValueKind.Array } array)
            {
                return [];
            }

            var rows = new List<ExtractedRow>();
            foreach (var element in array.EnumerateArray())
            {
                if (rows.Count >= search.Rows.MaxRows)
                {
                    break;
                }

                rows.Add(ExtractRow(element, search.Fields));
            }

            return rows;
        }
    }

    private static ExtractedRow ExtractRow(JsonElement row, DefinitionFields fields) => new(
        ReadField(row, fields.Title),
        ReadField(row, fields.DownloadUrl),
        ReadField(row, fields.SizeBytes),
        ReadField(row, fields.Seeders),
        ReadField(row, fields.PublishedAt),
        ReadField(row, fields.Leechers));

    private static string? ReadField(JsonElement row, DefinitionFieldRule? rule)
    {
        if (rule is null)
        {
            return null;
        }

        var node = NavigatePath(row, rule.Selector);
        return node switch
        {
            { ValueKind: JsonValueKind.String } value => value.GetString(),
            { ValueKind: JsonValueKind.Number } value => value.GetRawText(),
            _ => null,
        };
    }

    /// <summary>Walks a dotted chain of object property names (e.g. <c>"data.rows"</c>). No array indexing.</summary>
    private static JsonElement? NavigatePath(JsonElement root, string path)
    {
        var current = root;
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out var next))
            {
                return null;
            }

            current = next;
        }

        return current;
    }
}
