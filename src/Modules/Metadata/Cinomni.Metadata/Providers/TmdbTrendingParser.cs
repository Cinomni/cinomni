using System.Globalization;
using System.Text.Json;
using Cinomni.Metadata.Contracts;

namespace Cinomni.Metadata.Providers;

/// <summary>
/// Reads a TMDB trending payload. The document is hostile: a missing id is skipped, a title is
/// truncated, and a result that is not an object is ignored. Nothing here trusts a count the payload
/// claims — <paramref name="limit"/> is the cap.
/// </summary>
internal static class TmdbTrendingParser
{
    public const int TitleMaxLength = 500;
    public const int ExternalIdMaxLength = 20;

    public static IReadOnlyList<TrendingTitle> Parse(JsonElement root, MetadataMediaKind kind, int limit)
    {
        if (limit < 1)
        {
            return [];
        }

        var titleProperty = kind == MetadataMediaKind.Series ? "name" : "title";
        var originalProperty = kind == MetadataMediaKind.Series ? "original_name" : "original_title";
        var dateProperty = kind == MetadataMediaKind.Series ? "first_air_date" : "release_date";
        var titles = new List<TrendingTitle>();

        foreach (var item in ProviderJson.Array(root, "results"))
        {
            if (titles.Count >= limit)
            {
                break;
            }

            var id = ProviderJson.Int(item, "id");
            var title = ProviderJson.String(item, titleProperty) ?? ProviderJson.String(item, originalProperty);
            if (id is null || string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var externalId = id.Value.ToString(CultureInfo.InvariantCulture);
            if (externalId.Length == 0 || externalId.Length > ExternalIdMaxLength)
            {
                continue;
            }

            var trimmed = title.Trim();
            if (trimmed.Length > TitleMaxLength)
            {
                trimmed = trimmed[..TitleMaxLength];
            }

            titles.Add(new TrendingTitle(
                "tmdb",
                externalId,
                trimmed,
                ProviderJson.Year(ProviderJson.String(item, dateProperty)),
                kind));
        }

        return titles;
    }
}
