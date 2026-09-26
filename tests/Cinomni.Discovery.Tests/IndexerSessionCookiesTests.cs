using System.Net;
using System.Text;
using Cinomni.Discovery.Indexers.Definition;

namespace Cinomni.Discovery.Tests;

/// <summary>
/// Pure tests for the session jar: what gets captured from Set-Cookie headers, what the next request
/// sends, and what survives storage. No HTTP, no database — the jar is the fiddly, security-relevant
/// part of the session (it is the account in cookie form), so it is unit-tested directly.
/// </summary>
public sealed class IndexerSessionCookiesTests
{
    [Fact]
    public void Captures_every_set_cookie_header_into_the_jar()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.Add("Set-Cookie", "uid=42; Path=/; HttpOnly");
        response.Headers.Add("Set-Cookie", "pass=abc; Domain=idx.example; Path=/");

        var jar = IndexerSessionCookies.Merge(
            [], IndexerSessionCookies.Capture(response, new Uri("https://idx.example/login")));

        Assert.Equal(2, jar.Count);
        Assert.Equal("uid", jar[0].Name);
        Assert.Equal("42", jar[0].Value);
        Assert.Equal("/", jar[0].Path);
        Assert.Null(jar[0].Domain);
        Assert.Equal("idx.example", jar[1].Domain);
    }

    [Fact]
    public void Refuses_a_cookie_scoped_to_another_domain()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.Add("Set-Cookie", "session=x; Domain=other.example");

        Assert.Empty(IndexerSessionCookies.Capture(response, new Uri("https://idx.example/login")));
    }

    [Fact]
    public void Accepts_a_cookie_scoped_to_a_parent_domain_of_the_request_host()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.Add("Set-Cookie", "session=x; Domain=example.org");

        var jar = IndexerSessionCookies.Merge(
            [], IndexerSessionCookies.Capture(response, new Uri("https://tracker.example.org/login")));

        Assert.Single(jar);
    }

    [Theory]
    [InlineData("novalue")]
    [InlineData("=emptyname")]
    [InlineData("name=;")]
    [InlineData("bad=one,two")] // a comma would break the request header the value is joined into
    [InlineData("bad=one two")] // RFC 6265 excludes the space from cookie-octet, and so do we
    public void Refuses_malformed_or_header_breaking_cookies(string header)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.Add("Set-Cookie", header);

        Assert.Empty(IndexerSessionCookies.Capture(response, new Uri("https://idx.example/login")));
    }

    [Fact]
    public void A_reissued_cookie_replaces_the_one_it_supersedes()
    {
        // Rotating the session id at sign-in is what a site does correctly, to defeat session
        // fixation. A jar that appended would send both afterwards, and which one the server reads
        // is its own business — on a stack that takes the first, every search would be anonymous.
        var loginPage = new HttpResponseMessage(HttpStatusCode.OK);
        loginPage.Headers.Add("Set-Cookie", "SESSID=pre-auth; Path=/");
        var submit = new HttpResponseMessage(HttpStatusCode.Found);
        submit.Headers.Add("Set-Cookie", "SESSID=post-auth; Path=/");
        var origin = new Uri("https://idx.example/login");

        var jar = IndexerSessionCookies.Merge([], IndexerSessionCookies.Capture(loginPage, origin));
        jar = IndexerSessionCookies.Merge(jar, IndexerSessionCookies.Capture(submit, origin));

        var kept = Assert.Single(jar);
        Assert.Equal("post-auth", kept.Value);
        Assert.Equal("SESSID=post-auth", IndexerSessionCookies.HeaderFor(jar, new Uri("https://idx.example/search")));
    }

    [Fact]
    public void A_cleared_cookie_leaves_the_jar()
    {
        // Max-Age=0 and a past Expires are how a site deletes a cookie. Stored as an ordinary entry
        // they would be replayed for ever; and the empty value they arrive with must not be read as
        // a malformed header, or the entry being cleared would simply survive.
        var set = new HttpResponseMessage(HttpStatusCode.OK);
        set.Headers.Add("Set-Cookie", "a=1; Path=/");
        set.Headers.Add("Set-Cookie", "b=2; Path=/");
        var clear = new HttpResponseMessage(HttpStatusCode.OK);
        clear.Headers.Add("Set-Cookie", "a=; Path=/; Max-Age=0");
        clear.Headers.Add("Set-Cookie", "b=; Path=/; Expires=Thu, 01 Jan 1970 00:00:00 GMT");
        var origin = new Uri("https://idx.example/login");

        var jar = IndexerSessionCookies.Merge([], IndexerSessionCookies.Capture(set, origin));
        jar = IndexerSessionCookies.Merge(jar, IndexerSessionCookies.Capture(clear, origin));

        Assert.Empty(jar);
    }

    [Fact]
    public void A_path_scoped_cookie_stops_at_a_segment_boundary()
    {
        // RFC 6265 path matching, not a raw prefix: /user and /userpanel are different areas.
        var jar = new[] { new JarCookie("scoped", "1", Path: "/user") };

        Assert.NotNull(IndexerSessionCookies.HeaderFor(jar, new Uri("https://idx.example/user")));
        Assert.NotNull(IndexerSessionCookies.HeaderFor(jar, new Uri("https://idx.example/user/profile")));
        Assert.Null(IndexerSessionCookies.HeaderFor(jar, new Uri("https://idx.example/userpanel")));
    }

    [Fact]
    public void Builds_the_cookie_header_for_the_request_path()
    {
        var jar = new[]
        {
            new JarCookie("root", "1", Path: "/"),
            new JarCookie("deep", "2", Path: "/forum"),
            new JarCookie("bare", "3"),
        };

        var header = IndexerSessionCookies.HeaderFor(jar, new Uri("https://idx.example/forum/thread/1"));

        Assert.NotNull(header);
        Assert.Contains("deep=2", header, StringComparison.Ordinal);
        Assert.Contains("root=1", header, StringComparison.Ordinal);
        Assert.Contains("bare=3", header, StringComparison.Ordinal);
        Assert.DoesNotContain("deep=2", IndexerSessionCookies.HeaderFor(jar, new Uri("https://idx.example/search?q=x"))!, StringComparison.Ordinal);
    }

    [Fact]
    public void No_header_when_the_jar_is_empty_or_nothing_applies()
    {
        Assert.Null(IndexerSessionCookies.HeaderFor(null, new Uri("https://idx.example/search")));
        Assert.Null(IndexerSessionCookies.HeaderFor([], new Uri("https://idx.example/search")));
        Assert.Null(IndexerSessionCookies.HeaderFor(
            [new JarCookie("deep", "2", Path: "/forum")], new Uri("https://idx.example/search?q=x")));
    }

    [Fact]
    public void Round_trips_through_storage()
    {
        var jar = new[]
        {
            new JarCookie("uid", "42", Path: "/", Domain: "idx.example"),
            // '=' and '~' are legal inside a value and must survive the round trip untouched; the
            // space that is not legal is refused at capture, which the theory above pins.
            new JarCookie("pass", "a+b=c~d"),
        };

        var restored = IndexerSessionCookies.Deserialize(IndexerSessionCookies.Serialize(jar));

        Assert.Equal(jar, restored);
    }

    [Fact]
    public void Unreadable_or_malformed_storage_degrades_to_an_empty_jar()
    {
        // Every failure reads as "no session": the next search signs in again, which is exactly what
        // an indexer with no stored session already does.
        Assert.Empty(IndexerSessionCookies.Deserialize(null));
        Assert.Empty(IndexerSessionCookies.Deserialize("   "));
        Assert.Empty(IndexerSessionCookies.Deserialize("not json at all"));
        Assert.Empty(IndexerSessionCookies.Deserialize("{\"name\":\"uid\"}")); // an object, not an array
        Assert.Empty(IndexerSessionCookies.Deserialize("[{\"name\":\"\",\"value\":\"x\"}]"));
    }
}
