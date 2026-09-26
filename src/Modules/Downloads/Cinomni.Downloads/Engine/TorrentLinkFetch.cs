using System.Net;
using Cinomni.Kernel.Diagnostics;
using Cinomni.Kernel.Net;
using Microsoft.Extensions.Logging;

namespace Cinomni.Downloads.Engine;

/// <summary>What an indexer link turned out to be: a .torrent file, or a magnet it redirected to.</summary>
/// <param name="File">The .torrent bytes, or null when the link redirected to a magnet.</param>
/// <param name="Magnet">The magnet the link redirected to, or null when it served a file.</param>
public sealed record FetchedTorrent(byte[]? File, string? Magnet);

/// <summary>
/// Fetches the .torrent behind an indexer's download link over the SSRF-hardened client, following
/// redirects itself.
/// <para>
/// The hardened handler never follows a redirect, because the remote side chooses where one goes. That
/// leaves following them to the caller, and a caller that does not turns every torrent cache that moved
/// from http to https — and every indexer whose download link answers with a magnet — into a download
/// that fails for no reason the operator can act on. So each hop is followed here and its destination
/// checked exactly as the first link was: public http(s) only, no credentials in the URL, a bounded
/// number of hops. A <c>magnet:</c> destination ends the walk and is handed back as a magnet.
/// </para>
/// <para>
/// No failure quotes a URL: an indexer link carries the account's API key in its query string, and a
/// redirect may repeat it.
/// </para>
/// </summary>
public static class TorrentLinkFetch
{
    /// <summary>How many redirects one link may take before the fetch gives up.</summary>
    public const int MaxRedirects = 5;

    public static async Task<FetchedTorrent> FetchAsync(
        HttpClient http, string url, ILogger logger, CancellationToken cancellationToken)
    {
        if (!SsrfGuard.TryValidatePublicUrl(url, out var uri))
        {
            RecordRefusal();
            throw new InvalidOperationException("Refusing to fetch a .torrent from a non-public or non-http url.");
        }

        for (var hop = 0; ; hop++)
        {
            // The host, never the URL: the query string carries the account's API key.
            logger.LogDebug("Fetching .torrent from {Host}.", uri.Host);
            using var response = await http.GetAsync(uri, cancellationToken);
            if (!IsRedirect(response.StatusCode))
            {
                response.EnsureSuccessStatusCode();
                return new FetchedTorrent(await response.Content.ReadAsByteArrayAsync(cancellationToken), null);
            }

            if (hop == MaxRedirects)
            {
                throw new InvalidOperationException(
                    $"The download link took too many redirects (more than {MaxRedirects}); the .torrent was not fetched.");
            }

            var next = Resolve(uri, Location(response));
            if (next.Magnet is { } magnet)
            {
                return new FetchedTorrent(null, magnet);
            }

            uri = next.Uri!;
        }
    }

    /// <summary>
    /// The raw Location header. Not <c>Headers.Location</c>: that parses a path such as <c>/files/7.torrent</c>
    /// as an absolute <c>file://</c> URI on Linux, where this runs, instead of a path on the same host.
    /// </summary>
    private static string? Location(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Location", out var values) ? values.FirstOrDefault()?.Trim() : null;

    private static (Uri? Uri, string? Magnet) Resolve(Uri current, string? location)
    {
        if (string.IsNullOrEmpty(location))
        {
            throw new InvalidOperationException("The download link redirected without saying where to.");
        }

        if (location.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            return (null, location);
        }

        // Combined against the page that sent it, so a relative path or a scheme-relative //host stays
        // http(s); an absolute location of any other scheme survives the combine and the guard refuses it.
        if (!Uri.TryCreate(current, location, out var target)
            || !SsrfGuard.TryValidatePublicUrl(target.AbsoluteUri, out var validated))
        {
            RecordRefusal();
            throw new InvalidOperationException(
                "The download link redirected to a non-public or non-http address; the redirect was not followed.");
        }

        return (validated, null);
    }

    private static bool IsRedirect(HttpStatusCode status) => status is HttpStatusCode.MovedPermanently
        or HttpStatusCode.Found
        or HttpStatusCode.SeeOther
        or HttpStatusCode.TemporaryRedirect
        or HttpStatusCode.PermanentRedirect;

    // Counted, never quoted: see the class remarks.
    private static void RecordRefusal() => NetworkGuardMetrics.RecordRejection(
        NetworkGuardMetrics.Reasons.InvalidUrl, CinomniTelemetry.Modules.Downloads);
}
