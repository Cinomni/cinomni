using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;

namespace Cinomni.Kernel.Net;

/// <summary>
/// SSRF defense for outbound fetches to user-supplied hosts. Indexer base URLs
/// and torrent links are hostile: a request must never reach an internal, loopback, link-local or
/// cloud-metadata address. Host names are validated against the resolved IP at connect time
/// (anti-rebinding), so this classifies raw <see cref="IPAddress"/> values as well as
/// parsing/validating URLs. Shared platform utility — used by Discovery (indexer fetch) and
/// Downloads (fetching a .torrent from an indexer link).
/// </summary>
public static class SsrfGuard
{
    /// <summary>
    /// IANA special-purpose blocks with no public destination behind them, beyond the ones classified
    /// inline below. The sidecar and the indexer egress proxy refuse them through Python's
    /// <c>is_global</c>; without them here, the .NET side let through what the sidecar blocks.
    /// <para>
    /// Stricter than <c>is_global</c> on purpose in two places: all of 192.0.0.0/24 and 2001::/23 are
    /// refused, including the handful of anycast service addresses the registry marks global inside
    /// them (192.0.0.9, 2001:1::1, 2001:4:112::/48 …). None of them is an indexer or a tracker.
    /// </para>
    /// </summary>
    private static readonly (byte[] Prefix, int Length)[] SpecialPurposeV4 =
    [
        ([192, 0, 0, 0], 24),     // IETF protocol assignments (incl. DS-Lite 192.0.0.0/29)
        ([192, 0, 2, 0], 24),     // TEST-NET-1
        ([198, 18, 0, 0], 15),    // benchmarking
        ([198, 51, 100, 0], 24),  // TEST-NET-2
        ([203, 0, 113, 0], 24),   // TEST-NET-3
    ];

    private static readonly (byte[] Prefix, int Length)[] SpecialPurposeV6 =
    [
        ([0x00, 0x64, 0xFF, 0x9B, 0x00, 0x01], 48),  // 64:ff9b:1::/48 local-use IPv4/IPv6 translation
        ([0x01, 0x00, 0, 0, 0, 0, 0, 0], 64),        // 100::/64 discard-only
        ([0x20, 0x01, 0x00, 0x00], 23),              // 2001::/23 IETF protocol assignments (incl. Teredo 2001::/32)
        ([0x20, 0x01, 0x0D, 0xB8], 32),              // 2001:db8::/32 documentation
        ([0x20, 0x02], 16),                          // 2002::/16 6to4, which embeds an arbitrary IPv4 address
        ([0x3F, 0xFF, 0x00], 20),                    // 3fff::/20 documentation
    ];

    /// <summary>True if connecting to this address must be refused (non-public destination).</summary>
    public static bool IsBlockedAddress(IPAddress address)
    {
        // Normalize IPv4-mapped IPv6 (::ffff:a.b.c.d) to its IPv4 form and re-check.
        if (address.IsIPv4MappedToIPv6)
        {
            return IsBlockedAddress(address.MapToIPv4());
        }

        if (IPAddress.IsLoopback(address)
            || address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.IPv6Any)
            || address.IsIPv6LinkLocal
            || address.IsIPv6SiteLocal
            || address.IsIPv6Multicast)
        {
            return true;
        }

        var bytes = address.GetAddressBytes();

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            if (IsInAny(bytes, SpecialPurposeV4))
            {
                return true;
            }

            return bytes[0] switch
            {
                0 => true,                                   // 0.0.0.0/8 "this network"
                10 => true,                                  // 10.0.0.0/8 private
                127 => true,                                 // loopback
                100 => (bytes[1] & 0xC0) == 64,              // 100.64.0.0/10 CGNAT
                169 => bytes[1] == 254,                      // 169.254.0.0/16 link-local incl. 169.254.169.254 metadata
                172 => (bytes[1] & 0xF0) == 16,             // 172.16.0.0/12 private
                192 => bytes[1] == 168,                      // 192.168.0.0/16 private
                >= 224 => true,                              // 224.0.0.0/4 multicast + 240.0.0.0/4 reserved
                _ => false,
            };
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // Embedded-IPv4 forms that could smuggle an internal target through IPv6: deprecated
            // IPv4-compatible ::/96 and NAT64 64:ff9b::/96. (:: and ::1 are already handled above.)
            var isIPv4Compatible = bytes.Take(12).All(b => b == 0);
            var isNat64 = bytes[0] == 0x00 && bytes[1] == 0x64 && bytes[2] == 0xFF && bytes[3] == 0x9B
                && bytes.Skip(4).Take(8).All(b => b == 0);
            // IPv4-translated ::ffff:0:a.b.c.d (SIIT), which the IPv4-mapped check above does not cover.
            var isTranslated = bytes.Take(8).All(b => b == 0) && bytes[8] == 0xFF && bytes[9] == 0xFF
                && bytes[10] == 0 && bytes[11] == 0;
            if (isIPv4Compatible || isNat64 || isTranslated)
            {
                return IsBlockedAddress(new IPAddress(bytes[12..16]));
            }

            // Unique local addresses fc00::/7.
            return (bytes[0] & 0xFE) == 0xFC || IsInAny(bytes, SpecialPurposeV6);
        }

        // Anything that is not a routable IPv4/IPv6 address is refused.
        return true;
    }

    private static bool IsInAny(byte[] address, (byte[] Prefix, int Length)[] blocks)
    {
        foreach (var (prefix, length) in blocks)
        {
            if (HasPrefix(address, prefix, length))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasPrefix(byte[] address, byte[] prefix, int length)
    {
        var whole = length / 8;
        for (var i = 0; i < whole; i++)
        {
            if (address[i] != prefix[i])
            {
                return false;
            }
        }

        var rest = length % 8;
        if (rest == 0)
        {
            return true;
        }

        var mask = (byte)(0xFF << (8 - rest));
        return (address[whole] & mask) == (prefix[whole] & mask);
    }

    /// <summary>
    /// Validates that <paramref name="url"/> is an absolute http(s) URL and, when the host is an
    /// IP literal, that it is public. Host names pass here and are re-checked at connect time.
    /// </summary>
    public static bool TryValidatePublicUrl(string? url, [NotNullWhen(true)] out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed))
        {
            return false;
        }

        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(parsed.UserInfo))
        {
            return false;
        }

        if (IPAddress.TryParse(parsed.DnsSafeHost, out var literal) && IsBlockedAddress(literal))
        {
            return false;
        }

        uri = parsed;
        return true;
    }
}
