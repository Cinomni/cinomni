using Cinomni.Discovery.Contracts;

namespace Cinomni.Discovery.Indexers.Definition;

/// <summary>
/// How a definition's search response is shaped. Deliberately closed: adding a third format is a
/// vocabulary change, not something a definition can request on its own.
/// </summary>
internal enum DefinitionResponseFormat
{
    Html = 1,
    Json = 2,
}

/// <summary>The HTTP method a definition's request template uses.</summary>
internal enum DefinitionHttpMethod
{
    Get = 1,
    Post = 2,
}

/// <summary>
/// How a matched node's value is read. Closed vocabulary: an HTML selector yields an element, and
/// this says which part of it is the value.
/// </summary>
internal enum DefinitionFieldAttribute
{
    Text = 1,
    Href = 2,
    Src = 3,

    /// <summary>
    /// The element's <c>value</c> attribute. A hidden input is where a login page almost always
    /// keeps its CSRF token, and where a result row sometimes keeps an id the visible text does not.
    /// </summary>
    Value = 4,
}

/// <summary>
/// The named, closed vocabulary of value transforms a field rule may apply. There is deliberately
/// no scripting or regex-replace pipeline here — see <see cref="FieldTransforms"/>.
/// </summary>
internal enum DefinitionFieldTransform
{
    None = 0,
    ParseInt = 1,
    ParseSize = 2,
    ParseDate = 3,
    Trim = 4,
    UrlEncode = 5,
    ResolveRelativeUrl = 6,
}

/// <summary>
/// How to extract one field's raw value from a matched row, and how to turn it into the typed
/// value a <see cref="ReleaseCandidate"/> field expects.
/// </summary>
/// <param name="DateFormat">Required when <paramref name="Transform"/> is <see cref="DefinitionFieldTransform.ParseDate"/>.</param>
internal sealed record DefinitionFieldRule(
    string Selector,
    DefinitionFieldAttribute Attribute,
    DefinitionFieldTransform Transform = DefinitionFieldTransform.None,
    string? DateFormat = null);

/// <summary>The extraction rules for one result row. Every field a <see cref="ReleaseCandidate"/> needs.</summary>
internal sealed record DefinitionFields(
    DefinitionFieldRule Title,
    DefinitionFieldRule DownloadUrl,
    DefinitionFieldRule? SizeBytes = null,
    DefinitionFieldRule? Seeders = null,
    DefinitionFieldRule? PublishedAt = null,
    DefinitionFieldRule? Leechers = null);

/// <summary>
/// Where the result rows live in the response, and a server-enforced ceiling on how many a
/// definition may ever claim — never trusted from the definition alone at engine time.
/// </summary>
internal sealed record DefinitionRowRule(string Selector, int MaxRows);

/// <summary>Optional bounded follow-up page used to resolve a final download link.</summary>
internal sealed record DefinitionDetails(DefinitionFieldRule DownloadUrl, int MaxRequests);

/// <summary>
/// One request template for a subset of content kinds (e.g. <c>Movie</c>, or <c>Series</c>/
/// <c>Season</c>/<c>Episode</c> together). <paramref name="UrlTemplate"/> uses a restricted
/// placeholder syntax (<c>{{term}}</c>, <c>{{category}}</c>) — never a general template engine.
/// </summary>
internal sealed record DefinitionSearchRequest(
    IReadOnlyList<string> ContentKinds,
    DefinitionHttpMethod Method,
    string UrlTemplate,
    IReadOnlyDictionary<string, string>? CategoryMap = null);

/// <summary>The whole search side of a definition: how to ask, how to read the answer.</summary>
internal sealed record DefinitionSearch(
    IReadOnlyList<DefinitionSearchRequest> Requests,
    DefinitionResponseFormat ResponseFormat,
    DefinitionRowRule Rows,
    DefinitionFields Fields,
    DefinitionDetails? Details = null);

/// <summary>One form field to submit at login, e.g. <c>{{credential.username}}</c>.</summary>
internal sealed record DefinitionLoginField(string Name, string ValueTemplate);

/// <summary>Where to read a CSRF token out of the login page before submitting it.</summary>
internal sealed record DefinitionCsrfToken(string Selector, DefinitionFieldAttribute Attribute);

/// <summary>
/// A CSS selector that <b>only a logged-out response matches</b> — typically the site's login form.
/// Applied to a search or detail response, a match means the site expired the session; applied to a
/// non-redirect login response, it means the sign-in itself failed. A definition that omits it falls
/// back to status codes alone (401/403) for expiry.
/// </summary>
internal sealed record DefinitionSessionCheck(string Selector);

/// <summary>
/// The login sequence a definition declares, which the engine executes against a stored credential:
/// an optional CSRF read from the login page, then the form submit, capturing the cookies the
/// sequence sets. <paramref name="UrlTemplate"/> may not use placeholders (the login URL is fixed);
/// <paramref name="Fields"/> may reference <c>{{credential.username}}</c> and
/// <c>{{credential.password}}</c> only.
/// </summary>
internal sealed record DefinitionSessionLogin(
    DefinitionHttpMethod Method,
    string UrlTemplate,
    IReadOnlyList<DefinitionLoginField> Fields,
    DefinitionCsrfToken? CsrfToken = null);

/// <summary>
/// A definition's optional session block. Absent means the site needs no login. Present, the engine
/// signs in with the indexer's stored credential, keeps the cookies it captured, and re-signs-in when
/// a search response says the site expired the session (status 401/403 or <paramref name="Check"/>
/// matching). A definition with no credential to sign in with is searched exactly as if the block
/// were absent.
/// </summary>
internal sealed record DefinitionSession(DefinitionSessionLogin Login, DefinitionSessionCheck? Check = null);

/// <summary>
/// A validated, in-memory declarative indexer definition: how to search and parse a site that
/// speaks neither Torznab nor Newznab. Independently designed (own JSON format, closed transform
/// vocabulary, no embedded scripting) — see "Clean-room implementation" in CONTRIBUTING.md; this is not an
/// interpreter for any third-party YAML dialect, and no community definition files are consumed.
/// </summary>
internal sealed record IndexerDefinitionDocument(
    int SchemaVersion,
    ReleaseProtocol ResultKind,
    DefinitionSearch Search,
    DefinitionSession? Session = null);
