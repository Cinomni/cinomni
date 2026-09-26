using Cinomni.Identity.Api;
using Microsoft.AspNetCore.Http;

namespace Cinomni.Identity.Tests;

/// <summary>
/// A session token in the URL is accepted only where a media element has no other way to send it: a
/// read of a playback stream. Anywhere else it would land in proxy and access logs as a working link.
/// </summary>
public sealed class QueryTokenScopeTests
{
    [Theory]
    [InlineData("GET", "/api/playback/sessions/0199aaaa-0000-7000-8000-000000000001/stream")]
    [InlineData("HEAD", "/api/playback/sessions/0199aaaa-0000-7000-8000-000000000001/stream")]
    [InlineData("GET", "/api/playback/sessions/0199aaaa-0000-7000-8000-000000000001/hls/manifest.m3u8")]
    [InlineData("GET", "/api/playback/sessions/0199aaaa-0000-7000-8000-000000000001/hls/segment00042.ts")]
    public void A_playback_stream_read_may_carry_the_token_in_the_url(string method, string path) =>
        Assert.True(OpaqueTokenAuthenticationHandler.AcceptsQueryToken(method, new PathString(path)));

    [Theory]
    [InlineData("POST", "/api/playback/sessions/0199aaaa-0000-7000-8000-000000000001/stop")]
    [InlineData("GET", "/api/playback/sessions/0199aaaa-0000-7000-8000-000000000001")]
    [InlineData("POST", "/api/playback/sessions/0199aaaa-0000-7000-8000-000000000001/stream")]
    [InlineData("GET", "/api/realtime/stream")]
    [InlineData("GET", "/api/identity/me")]
    [InlineData("DELETE", "/api/identity/users/0199aaaa-0000-7000-8000-000000000001")]
    [InlineData("GET", "/api/playback/sessions/x/hls/a/b")]
    public void Nothing_else_accepts_a_token_in_the_url(string method, string path) =>
        Assert.False(OpaqueTokenAuthenticationHandler.AcceptsQueryToken(method, new PathString(path)));
}
