using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Cinomni.Kernel.Results;
using Cinomni.Search.Contracts;

namespace Cinomni.Discovery.Indexers.Definition;

/// <summary>The concrete, ready-to-issue request for one criterion against one definition.</summary>
internal sealed record DefinitionSearchQuery(DefinitionHttpMethod Method, Uri Url);

/// <summary>
/// Composes the concrete request for one criterion against one indexer definition. Pure and side-
/// effect free, mirroring <see cref="TorznabQueryBuilder"/>: picking the matching request template,
/// substituting its placeholders and turning the result into an absolute <see cref="Uri"/> is the
/// fiddly part, so it lives here rather than inside the transport.
/// </summary>
internal static class DefinitionQueryBuilder
{
    public static Result<DefinitionSearchQuery> Build(
        IndexerDefinitionDocument document, SearchCriterion criterion, Uri? baseUri = null)
    {
        var request = document.Search.Requests.FirstOrDefault(r =>
            r.ContentKinds.Any(kind => string.Equals(kind, criterion.ContentKind, StringComparison.OrdinalIgnoreCase)));

        if (request is null)
        {
            // Mirrors TorznabQueryBuilder's own reasoning: ReleaseSearch.SearchOneAsync swallows every
            // exception and returns no candidates, so a definition with no request for this content
            // kind must be indistinguishable from a genuine "nothing available", not a crash.
            return Result<DefinitionSearchQuery>.Failure(new Error(
                "discovery.definition.query.no_matching_request",
                $"Definition declares no search request for content kind '{criterion.ContentKind}'."));
        }

        var category = request.CategoryMap is not null && request.CategoryMap.TryGetValue(criterion.ContentKind, out var mapped)
            ? mapped
            : string.Empty;

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["term"] = TermValue(criterion),
            ["category"] = category,
        };

        var url = Substitute(request.UrlTemplate, values);
        Uri? uri;
        var validUri = baseUri is not null
            ? TryResolve(baseUri, url, out uri)
            : Uri.TryCreate(url, UriKind.Absolute, out uri) && IsHttp(uri);
        if (!validUri || uri is null)
        {
            return Result<DefinitionSearchQuery>.Failure(new Error(
                "discovery.definition.query.invalid_url", $"Substituted URL '{url}' is not a valid absolute URL."));
        }

        if (baseUri is not null && !SameOrigin(baseUri, uri))
        {
            return Result<DefinitionSearchQuery>.Failure(new Error(
                "discovery.definition.query.cross_origin", "Definition request must stay on the configured base URL origin."));
        }

        return Result<DefinitionSearchQuery>.Success(new DefinitionSearchQuery(request.Method, uri));
    }

    /// <summary>
    /// Resolves a link found on, or written for, an indexer's site into an http(s) address, the same on
    /// every operating system. On Linux and macOS a rooted path such as <c>/dl/1.torrent</c> parses as an
    /// absolute <c>file:///</c> URI, so trying "is it absolute?" first sent every root-relative link down
    /// the wrong road — refused as cross-origin, or pointed at the local disk.
    /// </summary>
    internal static bool TryResolve(Uri baseUri, string value, [NotNullWhen(true)] out Uri? resolved)
    {
        resolved = null;
        var candidate = value.Trim();
        if (candidate.Length == 0)
        {
            return false;
        }

        if (candidate.StartsWith('/') && !candidate.StartsWith("//", StringComparison.Ordinal))
        {
            // Root-relative: the site's own origin plus this path, never a file path.
            return Uri.TryCreate(baseUri.GetLeftPart(UriPartial.Authority) + candidate, UriKind.Absolute, out resolved)
                && IsHttp(resolved);
        }

        if (Uri.TryCreate(candidate, UriKind.Absolute, out var absolute) && !absolute.IsFile)
        {
            resolved = absolute;
            return IsHttp(resolved);
        }

        return Uri.TryCreate(baseUri, candidate, out resolved) && IsHttp(resolved);
    }

    private static bool IsHttp(Uri uri) => uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;

    internal static bool SameOrigin(Uri left, Uri right) =>
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase)
        && left.Port == right.Port;

    /// <summary>
    /// The numbering folded into the term — the definition format has no dedicated season/episode
    /// placeholder (v1), so every numbering family rides in the free-text term, same as Torznab's
    /// own <c>t=search</c> fallback.
    /// </summary>
    private static string TermValue(SearchCriterion criterion)
    {
        if (criterion.SeasonNumber is int season)
        {
            return criterion.EpisodeNumber is int episode
                ? $"{criterion.Term} S{season:00}E{episode:00}"
                : $"{criterion.Term} S{season:00}";
        }

        if (criterion.AbsoluteNumber is int absolute)
        {
            return $"{criterion.Term} {absolute.ToString(CultureInfo.InvariantCulture)}";
        }

        if (criterion.AirDate is DateOnly airDate)
        {
            return $"{criterion.Term} {airDate.ToString("yyyy MM dd", CultureInfo.InvariantCulture)}";
        }

        return criterion.Term;
    }

    /// <summary>
    /// Every substituted value is percent-encoded — a template placeholder is never a raw,
    /// unencoded splice into a URL, so a term containing <c>&amp;</c> or <c>#</c> cannot smuggle an
    /// extra query parameter. An unrecognized placeholder (shouldn't occur post-validation) is left
    /// untouched rather than throwing.
    /// </summary>
    private static string Substitute(string template, IReadOnlyDictionary<string, string> values) =>
        FieldTransforms.PlaceholderPattern().Replace(template, match =>
            values.TryGetValue(match.Groups[1].Value, out var value) ? FieldTransforms.UrlEncode(value) : match.Value);
}
