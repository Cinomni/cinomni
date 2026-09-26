using System.Globalization;
using System.Text.Json;

namespace Cinomni.Discovery.Indexers.Definition;

/// <summary>One cookie of a definition-backed indexer's session jar.</summary>
/// <param name="Path">The <c>Path</c> attribute the site sent, or null when it sent none.</param>
/// <param name="Domain">
/// The <c>Domain</c> attribute the site sent, leading dot stripped, or null. Cookies naming another
/// host are never captured: every request a definition issues is pinned to the base URL's origin, so
/// a cookie we could never send back is only stored garbage.
/// </param>
internal sealed record JarCookie(string Name, string Value, string? Path = null, string? Domain = null);

/// <summary>
/// One <c>Set-Cookie</c> header, read. A site does not only add cookies: it replaces them under the
/// same name, and deletes them by re-sending the name with <c>Max-Age=0</c> or an expiry in the past.
/// <see cref="IsRemoval"/> is what lets <see cref="IndexerSessionCookies.Merge"/> tell the third case
/// from the first two, instead of storing a deletion as a cookie whose value happens to be empty.
/// </summary>
internal sealed record CookieDirective(JarCookie Cookie, bool IsRemoval);

/// <summary>
/// The cookie jar of one indexer's session: captured from the <c>Set-Cookie</c> headers of every
/// response in the login sequence, stored as schema-validated JSON and encrypted at rest by
/// <c>IndexerCredentialProtector</c>, and sent back explicitly as a <c>Cookie</c> header. The
/// transport's own cookie container is off (the jar would otherwise rotate with the handler pool and
/// silently fight the explicit one), which is what makes capture and replay this type's whole job.
/// <para>
/// Values are secrets — a jar is the account in cookie form — so nothing here is ever logged and
/// nothing but the header the next request needs ever leaves the manager that holds the jar.
/// </para>
/// </summary>
internal static class IndexerSessionCookies
{
    /// <summary>A site can be hostile about its jar size too; a session never needs a thousand cookies.</summary>
    private const int MaxCookies = 100;

    /// <summary>One cookie component past this is not a session, it is an attempt to fill the database.</summary>
    private const int MaxComponentLength = 4096;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Every cookie the response asks the request's origin to keep, in header order. Invalid
    /// <c>Set-Cookie</c> parts are skipped, never thrown; a <c>Domain</c> attribute the request host
    /// does not match names a host the jar's requests could never be sent to, and is refused rather
    /// than stored.
    /// </summary>
    public static IReadOnlyList<CookieDirective> Capture(HttpResponseMessage response, Uri requestUri)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            return [];
        }

        var host = requestUri.DnsSafeHost;
        var directives = new List<CookieDirective>();
        foreach (var header in setCookies)
        {
            var directive = ParseSetCookie(header, host);
            if (directive is not null)
            {
                directives.Add(directive);
                if (directives.Count >= MaxCookies)
                {
                    break;
                }
            }
        }

        return directives;
    }

    /// <summary>
    /// The jar after one response's <c>Set-Cookie</c> headers are applied to it: the same name, path
    /// and domain replaces in place, a removal drops the entry, anything else is appended.
    /// <para>
    /// Replacing rather than appending is the whole point. Rotating the session id at sign-in is what
    /// a site does <em>correctly</em>, to defeat session fixation — and a jar that appended would then
    /// send <c>SESSID=&lt;pre-auth&gt;; SESSID=&lt;post-auth&gt;</c> on every search afterwards. Which of
    /// the two a server reads is its own business, so on any stack that takes the first one every
    /// authenticated request would silently be an anonymous one; and the pre-authentication
    /// identifier — the one an attacker fixates — would be kept encrypted and replayed for as long as
    /// the session lived.
    /// </para>
    /// </summary>
    public static IReadOnlyList<JarCookie> Merge(
        IReadOnlyList<JarCookie> jar, IReadOnlyList<CookieDirective> directives)
    {
        if (directives.Count == 0)
        {
            return jar;
        }

        var merged = jar.ToList();
        foreach (var directive in directives)
        {
            var existing = merged.FindIndex(cookie => SameSlot(cookie, directive.Cookie));
            if (directive.IsRemoval)
            {
                if (existing >= 0)
                {
                    merged.RemoveAt(existing);
                }
            }
            else if (existing >= 0)
            {
                merged[existing] = directive.Cookie;
            }
            else if (merged.Count < MaxCookies)
            {
                // The cap is on the merged jar, not on one response: the two responses of a login
                // sequence must not add up to twice what storage will ever read back.
                merged.Add(directive.Cookie);
            }
        }

        return merged;
    }

    /// <summary>
    /// The <c>Cookie</c> header for one request: the jar's cookies that may travel to this path,
    /// joined the way a browser would send them. Null when nothing applies — and then the request is
    /// sent without the header at all, never with an empty one.
    /// </summary>
    public static string? HeaderFor(IReadOnlyList<JarCookie>? jar, Uri requestUri)
    {
        if (jar is not { Count: > 0 })
        {
            return null;
        }

        var requestPath = requestUri.AbsolutePath;
        var header = string.Join(
            "; ",
            jar.Where(cookie => PathMatches(cookie, requestPath)).Select(cookie => $"{cookie.Name}={cookie.Value}"));
        return header.Length == 0 ? null : header;
    }

    /// <summary>The storage form of the jar. Encrypted before it touches the database, never before.</summary>
    public static string Serialize(IReadOnlyList<JarCookie> jar) => JsonSerializer.Serialize(jar, JsonOptions);

    /// <summary>
    /// The jar back from storage, read defensively: storage is trusted to a fault (it is our own
    /// ciphertext, authenticated), but the plaintext is still validated entry by entry, so a jar
    /// somehow holding malformed entries degrades to the cookies it does hold — and an unreadable one
    /// to an empty jar, which makes the next search sign in again. Never throws.
    /// </summary>
    public static IReadOnlyList<JarCookie> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            if (JsonSerializer.Deserialize<List<JarCookie>>(json, JsonOptions) is not { } jar)
            {
                return [];
            }

            return jar
                .Where(cookie => cookie is not null && IsValid(cookie.Name) && IsValid(cookie.Value))
                .Take(MaxCookies)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    internal static CookieDirective? ParseSetCookie(string header, string requestHost)
    {
        // The first pair before the first attribute separator is the cookie itself; the rest are
        // attributes. A value is never empty and never carries a character that would break the
        // request header the value is later joined into.
        var parts = header.Split(';');
        var pair = parts[0];
        var separator = pair.IndexOf('=');
        if (separator <= 0)
        {
            return null;
        }

        var name = pair[..separator].Trim();
        var value = pair[(separator + 1)..].Trim();
        if (!IsValid(name))
        {
            return null;
        }

        string? path = null;
        string? domain = null;
        var removal = false;
        foreach (var attribute in parts.Skip(1))
        {
            var trimmed = attribute.Trim();
            var equals = trimmed.IndexOf('=');
            var attributeName = (equals < 0 ? trimmed : trimmed[..equals]).Trim();
            var attributeValue = equals < 0 ? null : trimmed[(equals + 1)..].Trim();
            if (attributeName.Equals("Path", StringComparison.OrdinalIgnoreCase))
            {
                path = Bounded(attributeValue);
            }
            else if (attributeName.Equals("Domain", StringComparison.OrdinalIgnoreCase))
            {
                domain = Bounded(attributeValue?.TrimStart('.'));
            }
            else if (attributeName.Equals("Max-Age", StringComparison.OrdinalIgnoreCase))
            {
                removal |= long.TryParse(attributeValue, CultureInfo.InvariantCulture, out var seconds) && seconds <= 0;
            }
            else if (attributeName.Equals("Expires", StringComparison.OrdinalIgnoreCase))
            {
                removal |= DateTimeOffset.TryParse(
                    attributeValue, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var expires)
                    && expires <= DateTimeOffset.UtcNow;
            }
        }

        // Every request a definition issues is SameOrigin-pinned to the base URL the login was
        // resolved against, so a cookie scoped to any other domain could never be sent back — it is
        // refused, not stored as garbage the next search would carry nowhere.
        if (domain is { Length: > 0 } && !HostMatchesDomain(requestHost, domain))
        {
            return null;
        }

        // A deletion is identified by its name alone, so its value is not held to the same bar: a
        // site clearing a cookie sends an empty one, and refusing that as malformed would leave the
        // entry it was clearing in the jar for ever.
        if (!removal && !IsValid(value))
        {
            return null;
        }

        return new CookieDirective(new JarCookie(name, value, path, domain), removal);
    }

    /// <summary>Two entries are the same cookie when the site's own key for it matches: name, path and domain.</summary>
    private static bool SameSlot(JarCookie left, JarCookie right) =>
        string.Equals(left.Name, right.Name, StringComparison.Ordinal)
        && string.Equals(left.Path ?? "/", right.Path ?? "/", StringComparison.Ordinal)
        && string.Equals(left.Domain, right.Domain, StringComparison.OrdinalIgnoreCase);

    /// <summary>An attribute long enough to be an attempt to fill the row is dropped, not stored.</summary>
    private static string? Bounded(string? attributeValue) =>
        attributeValue is { Length: > 0 and <= MaxComponentLength } ? attributeValue : null;

    /// <summary>A name or value that would break the Cookie header format is refused outright.</summary>
    private static bool IsValid(string component) =>
        component.Length > 0
        && component.Length <= MaxComponentLength
        && !component.Contains(';', StringComparison.Ordinal)
        && !component.Contains(',', StringComparison.Ordinal)
        && component.All(character => character > ' ' && character < '');

    /// <summary>The host the cookie was set on is the bare domain itself or any subdomain of it.</summary>
    private static bool HostMatchesDomain(string host, string domain) =>
        host.Equals(domain, StringComparison.OrdinalIgnoreCase)
        || (host.EndsWith($".{domain}", StringComparison.OrdinalIgnoreCase) && domain.Length > 0);

    /// <summary>
    /// RFC 6265 path matching: the request path is the cookie's path, or sits under it at a segment
    /// boundary. A raw prefix test would send a <c>Path=/user</c> cookie to <c>/userpanel</c>, which
    /// is a different area of the same site.
    /// </summary>
    private static bool PathMatches(JarCookie cookie, string requestPath)
    {
        if (cookie.Path is not { Length: > 0 } path || path == "/")
        {
            return true;
        }

        if (!requestPath.StartsWith(path, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return requestPath.Length == path.Length
            || path.EndsWith('/')
            || requestPath[path.Length] == '/';
    }
}
