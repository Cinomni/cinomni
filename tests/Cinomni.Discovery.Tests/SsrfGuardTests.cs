using System.Net;
using Cinomni.Kernel.Net;

namespace Cinomni.Discovery.Tests;

/// <summary>Unit tests for the SSRF address/URL classifier.</summary>
public sealed class SsrfGuardTests
{
    [Theory]
    // Blocked: loopback, private, link-local/metadata, CGNAT, IPv6 internal + embedded IPv4.
    [InlineData("127.0.0.1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("192.168.1.1", true)]
    [InlineData("169.254.169.254", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("::1", true)]
    [InlineData("fc00::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("::ffff:10.0.0.1", true)]     // IPv4-mapped -> 10.0.0.1
    [InlineData("::7f00:1", true)]            // IPv4-compatible -> 127.0.0.1
    [InlineData("64:ff9b::7f00:1", true)]     // NAT64 -> 127.0.0.1
    [InlineData("192.0.0.1", true)]           // IETF protocol assignments
    [InlineData("192.0.2.1", true)]           // TEST-NET-1
    [InlineData("198.18.0.1", true)]          // benchmarking
    [InlineData("198.19.255.255", true)]
    [InlineData("198.51.100.7", true)]        // TEST-NET-2
    [InlineData("203.0.113.9", true)]         // TEST-NET-3
    [InlineData("64:ff9b:1::a00:1", true)]    // local-use translation
    [InlineData("100::1", true)]              // discard-only
    [InlineData("2001::1", true)]             // Teredo
    [InlineData("2001:db8::1", true)]         // documentation
    [InlineData("2002:7f00:1::1", true)]      // 6to4 around 127.0.0.1
    [InlineData("3fff::1", true)]             // documentation
    [InlineData("::ffff:198.18.0.1", true)]
    [InlineData("::ffff:0:a00:1", true)]      // IPv4-translated -> 10.0.0.1
    [InlineData("::ffff:0:808:808", false)]   // IPv4-translated -> 8.8.8.8
    [InlineData("198.20.0.1", false)]         // just past 198.18.0.0/15
    [InlineData("192.0.3.1", false)]
    [InlineData("2001:200::1", false)]        // just past 2001::/23
    // Allowed: genuine public addresses.
    [InlineData("8.8.8.8", false)]
    [InlineData("1.1.1.1", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("100.128.0.1", false)]
    [InlineData("2606:4700:4700::1111", false)]
    [InlineData("::ffff:8.8.8.8", false)]
    [InlineData("64:ff9b::808:808", false)]   // NAT64 -> 8.8.8.8
    public void Classifies_addresses(string ip, bool blocked) =>
        Assert.Equal(blocked, SsrfGuard.IsBlockedAddress(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("https://indexer.example/torznab", true)]
    [InlineData("http://8.8.8.8/torznab", true)]
    [InlineData("http://127.0.0.1/x", false)]
    [InlineData("http://169.254.169.254/latest/meta-data", false)]
    [InlineData("https://user@indexer.example/torznab", false)]
    [InlineData("https://user:secret@indexer.example/torznab", false)]
    [InlineData("ftp://example.com", false)]
    [InlineData("not a url", false)]
    [InlineData("", false)]
    public void Validates_base_urls(string url, bool expected) =>
        Assert.Equal(expected, SsrfGuard.TryValidatePublicUrl(url, out _));
}
