using AngleSharp.Html.Parser;

namespace Cinomni.Discovery.Indexers.Definition;

/// <summary>
/// Applies a definition's <see cref="DefinitionSessionCheck"/> selector — the CSS selector that only
/// a logged-out response matches — to a response body. The same check answers two questions: applied
/// to a search or detail response, a match means the site expired the session; applied to a
/// non-redirect login response, it means the sign-in itself failed.
/// <para>
/// A selector that AngleSharp cannot parse is treated as "no match" rather than an error: a broken
/// check must degrade the definition to status-code-only expiry detection, never break every search
/// on it. The response is operator-trustable content bounded upstream by the transport's size cap.
/// </para>
/// </summary>
internal static class DefinitionSessionMatcher
{
    public static bool IsLoggedOut(string html, string selector)
    {
        try
        {
            // One parser per call, like every other AngleSharp use in this module. A shared static
            // instance carries a shared browsing context, and this runs inside the parallel indexer
            // fan-out; AngleSharp documents no thread-safety guarantee for that, and a shared parser
            // is not worth finding out on somebody else's installation.
            return new HtmlParser().ParseDocument(html).QuerySelector(selector) is not null;
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            return false;
        }
    }
}
