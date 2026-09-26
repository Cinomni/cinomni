namespace Cinomni.Catalog.Application;

/// <summary>Derives a sort title: leading article dropped, lower-cased, so "The Matrix" sorts under "matrix".</summary>
internal static class SortTitles
{
    private static readonly string[] Articles = ["the ", "a ", "an "];

    public static string Normalize(string title)
    {
        var trimmed = title.Trim().ToLowerInvariant();
        foreach (var article in Articles)
        {
            if (trimmed.StartsWith(article, StringComparison.Ordinal))
            {
                return trimmed[article.Length..].Trim();
            }
        }

        return trimmed;
    }
}
