using System.Diagnostics;

namespace Cinomni.Host.Observability;

/// <summary>
/// Strips the attributes the automatic HTTP instrumentation would otherwise export verbatim, each of
/// which is a credential or an identity in this product.
/// <para>
/// <b>Incoming requests.</b> A playback stream read (<c>/stream</c> and <c>/hls/…</c>) authenticates with
/// an <c>access_token</c> query parameter, because a <c>&lt;video&gt;</c> element cannot set a header; no
/// other route accepts one, and the live stream is read with the header. That is a documented, deliberate
/// trade in <c>SECURITY.md</c> — and it means the query string of such a span is a bearer token. The host goes
/// too: it is whatever the caller put in the <c>Host</c> header, so it is both this installation's own
/// address and an unbounded attacker-chosen string.
/// </para>
/// <para>
/// <b>Outgoing requests.</b> An indexer, metadata or subtitle provider URL carries the account's API
/// key in its query string, and its host names the private tracker, the metadata provider or the
/// webhook a user supplied. For a self-hosted household that host is the most identifying
/// non-credential value in the installation, so a client span keeps the path and nothing else.
/// </para>
/// <para>
/// Values are dropped rather than masked, because a masked value still tells an observer that a
/// parameter exists and how long it was. What survives — a path, a method, a status, a duration — is
/// what makes a span answer "what was slow"; <b>which</b> provider it was is answered by the module's
/// own instruments, which tag the operator-chosen indexer name.
/// </para>
/// </summary>
internal static class TelemetryRedaction
{
    /// <summary>Server-span attribute holding the raw query string.</summary>
    private const string UrlQueryTag = "url.query";

    /// <summary>Client-span attribute holding the complete request URL.</summary>
    private const string UrlFullTag = "url.full";

    /// <summary>Peer host on both span kinds: the <c>Host</c> header inbound, the provider outbound.</summary>
    private const string ServerAddressTag = "server.address";

    /// <summary>Peer port. Dropped with the host; on its own it says which provider was called.</summary>
    private const string ServerPortTag = "server.port";

    /// <summary>
    /// Removes the query string and the caller-supplied host from an incoming-request span. Setting a
    /// tag to <see langword="null"/> removes it, so nothing downstream sees an empty-but-present
    /// attribute either.
    /// </summary>
    public static void RedactServerRequest(Activity activity)
    {
        activity.SetTag(UrlQueryTag, null);
        activity.SetTag(ServerAddressTag, null);
        activity.SetTag(ServerPortTag, null);
    }

    /// <summary>Rewrites an outgoing-request span so only the path is left of who was called.</summary>
    public static void RedactClientRequest(Activity activity, Uri? requestUri)
    {
        activity.SetTag(ServerAddressTag, null);
        activity.SetTag(ServerPortTag, null);

        if (requestUri is null || !requestUri.IsAbsoluteUri)
        {
            activity.SetTag(UrlFullTag, null);
            return;
        }

        activity.SetTag(UrlFullTag, requestUri.AbsolutePath);
    }
}
