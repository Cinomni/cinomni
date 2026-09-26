using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace Cinomni.Discovery.Indexers.Definition;

/// <summary>
/// Whether a definition's selector is one AngleSharp can run. Checked when a definition is parsed, so
/// a malformed selector is a named error at upload or dry run instead of an exception the first time a
/// search — or the <c>/validate</c> endpoint — tries to use it.
/// </summary>
internal static class CssSelectors
{
    /// <summary>Far beyond any real row or field selector; a bound on what reaches the compiler.</summary>
    internal const int MaxLength = 1024;

    /// <summary>
    /// Deepest nesting of parentheses and brackets accepted. AngleSharp compiles <c>:not(:not(…))</c> recursively,
    /// and a stack overflow cannot be caught: it ends the process rather than failing the upload.
    /// </summary>
    internal const int MaxNesting = 8;

    public static bool IsValid(string selector)
    {
        if (selector.Length > MaxLength || NestingOf(selector) > MaxNesting)
        {
            return false;
        }

        try
        {
            // An empty document is enough: the selector is compiled before anything is matched.
            new HtmlParser().ParseDocument(string.Empty).QuerySelector(selector);
            return true;
        }
        catch (DomException)
        {
            return false;
        }
    }

    private static int NestingOf(string selector)
    {
        var depth = 0;
        var deepest = 0;
        foreach (var ch in selector)
        {
            if (ch is '(' or '[')
            {
                deepest = Math.Max(deepest, ++depth);
            }
            else if ((ch is ')' or ']') && depth > 0)
            {
                depth--;
            }
        }

        return deepest;
    }
}
