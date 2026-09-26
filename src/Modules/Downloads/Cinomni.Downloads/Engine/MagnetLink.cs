using System.Net;
using System.Text;
using Cinomni.Kernel.Net;

namespace Cinomni.Downloads.Engine;

/// <summary>
/// A magnet rebuilt from what it means rather than passed on as it arrived. The link comes from an
/// indexer's feed — hostile input — and an external client may read more into the raw text than its
/// parameters: qBittorrent splits the <c>urls</c> it is sent on line breaks, so a feed that encodes one
/// (<c>&amp;#10;</c> in XML) could slip a second torrent or an internal URL in after the magnet.
/// <para>
/// What survives is the info-hash (in canonical hex), one display name with its control characters
/// removed and no path separators, and at most <see cref="MaxTrackers"/> announce URLs that pass the
/// sidecar's rule for its own, made stricter where two URL parsers disagree: a scheme trackers announce
/// over, nothing smuggled in the text, no user info, and no host that names this machine or its network
/// by address. Every value is escaped again on the way out, so what is sent is one link and nothing else.
/// </para>
/// </summary>
internal static class MagnetLink
{
    internal const int MaxTrackers = 20;

    private const int MaxTrackerUrlLength = 2048;
    private const int MaxDisplayNameLength = 255;

    private static readonly HashSet<string> TrackerSchemes = new(StringComparer.OrdinalIgnoreCase) { "http", "https", "udp" };

    /// <returns>The rebuilt link, or <c>null</c> when <paramref name="url"/> is not a magnet with a v1 info-hash.</returns>
    public static string? Canonical(string url)
    {
        if (TorrentInfoHash.FromMagnet(url) is not { } hash)
        {
            return null;
        }

        string? name = null;
        var trackers = new List<string>();
        foreach (var part in url["magnet:?".Length..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var key = part[..eq];
            var raw = part[(eq + 1)..];
            if (key.Equals("dn", StringComparison.OrdinalIgnoreCase) && name is null)
            {
                // A "+" in a query value is a space, as the torrent engine reads the raw magnet — the
                // name it shows, and the folder a download lands in, have to stay what they were.
                name = DisplayName(Uri.UnescapeDataString(raw.Replace('+', ' ')));
                continue;
            }

            if (IsTrackerKey(key) && trackers.Count < MaxTrackers && IsAcceptableTracker(Uri.UnescapeDataString(raw)))
            {
                trackers.Add(Uri.UnescapeDataString(raw));
            }
        }

        var link = new StringBuilder("magnet:?xt=urn:btih:").Append(hash);
        if (!string.IsNullOrEmpty(name))
        {
            link.Append("&dn=").Append(Uri.EscapeDataString(name));
        }

        foreach (var tracker in trackers)
        {
            link.Append("&tr=").Append(Uri.EscapeDataString(tracker));
        }

        return link.ToString();
    }

    /// <summary>
    /// The sidecar's rule for an announce URL (see <c>_is_acceptable_tracker</c>), and stricter where
    /// two parsers disagree: no <c>@</c>, <c>#</c> or <c>\</c> anywhere. .NET reads
    /// <c>udp://tracker.example:6969?a:@192.168.1.1</c> as a query on a public host, while the torrent
    /// engine reads everything up to the <c>@</c> as credentials and announces to the address after it.
    /// </summary>
    internal static bool IsAcceptableTracker(string url)
    {
        if (url.Length == 0 || url.Length > MaxTrackerUrlLength
            || url.Any(ch => char.IsWhiteSpace(ch) || char.IsControl(ch) || ch is '@' or '#' or '\\'))
        {
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var tracker) || !TrackerSchemes.Contains(tracker.Scheme)
            || !string.IsNullOrEmpty(tracker.UserInfo))
        {
            return false;
        }

        var host = tracker.DnsSafeHost.TrimEnd('.');
        if (host.Length == 0 || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.Equals("localhost.localdomain", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // A name passes: resolving it here would answer for this moment, not for the announce.
        return !IPAddress.TryParse(host, out var address) || !SsrfGuard.IsBlockedAddress(address);
    }

    private static bool IsTrackerKey(string key) =>
        key.Equals("tr", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("tr.", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A name to show, and no more: control characters dropped, and path separators replaced — the name
    /// can become the folder a download lands in, and a name is never a path.
    /// </summary>
    private static string DisplayName(string value)
    {
        var cleaned = new string([.. value
            .Where(ch => !char.IsControl(ch))
            .Select(ch => ch is '/' or '\\' ? '_' : ch)]).Trim();
        if (cleaned.Length <= MaxDisplayNameLength)
        {
            return cleaned;
        }

        // Never cut a surrogate pair in half.
        var length = char.IsHighSurrogate(cleaned[MaxDisplayNameLength - 1]) ? MaxDisplayNameLength - 1 : MaxDisplayNameLength;
        return cleaned[..length];
    }
}
