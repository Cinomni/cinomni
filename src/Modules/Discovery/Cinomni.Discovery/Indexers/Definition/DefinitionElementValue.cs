using AngleSharp.Dom;

namespace Cinomni.Discovery.Indexers.Definition;

/// <summary>
/// Reads one matched element through the closed attribute vocabulary a definition names. The three
/// places that resolve a rule — a result row, a detail page, and the login page's CSRF token — have
/// to agree on what <c>Href</c> or <c>Value</c> means, so they read through here instead of each
/// carrying its own copy of the same switch.
/// </summary>
internal static class DefinitionElementValue
{
    /// <summary>The element's value under this attribute, or null when there is no element or no such attribute.</summary>
    public static string? Read(IElement? element, DefinitionFieldAttribute attribute) => attribute switch
    {
        DefinitionFieldAttribute.Href => element?.GetAttribute("href"),
        DefinitionFieldAttribute.Src => element?.GetAttribute("src"),
        DefinitionFieldAttribute.Value => element?.GetAttribute("value"),
        _ => element?.TextContent,
    };
}
