using AngleSharp.Html.Parser;
using Cinomni.Kernel.Results;

namespace Cinomni.Discovery.Indexers.Definition;

internal static class DefinitionDetailParser
{
    public static Result<string> Extract(string html, DefinitionDetails details, Uri detailUri)
    {
        var element = new HtmlParser().ParseDocument(html).QuerySelector(details.DownloadUrl.Selector);
        var raw = DefinitionElementValue.Read(element, details.DownloadUrl.Attribute);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Result<string>.Failure(new Error(
                "discovery.definition.details.link_not_found", "The detail page did not contain a download link."));
        }

        var value = raw.Trim();
        if (value.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            return Result<string>.Success(value);
        }

        if (!DefinitionQueryBuilder.TryResolve(detailUri, value, out var resolved))
        {
            return Result<string>.Failure(new Error(
                "discovery.definition.details.invalid_link", "The detail page download link was invalid."));
        }

        return DefinitionQueryBuilder.SameOrigin(detailUri, resolved)
            ? Result<string>.Success(resolved.ToString())
            : Result<string>.Failure(new Error(
                "discovery.definition.details.cross_origin", "The detail page download link changed origin."));
    }
}
