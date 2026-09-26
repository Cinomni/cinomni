using System.Net;
using System.Text;
using Cinomni.Discovery.Indexers;

namespace Cinomni.Discovery.Tests;

public sealed class FlareSolverrClientTests
{
    [Fact]
    public async Task Request_always_contains_fixed_proxy_and_accepts_same_origin_success()
    {
        var handler = new BrowserHandler("""
            {"status":"ok","solution":{"status":200,"url":"https://8.8.8.8/search","response":"<html>ok</html>"}}
            """);
        var client = new FlareSolverrClient(new HttpClient(handler));

        var body = await client.FetchAsync(new Uri("https://8.8.8.8/"), new Uri("https://8.8.8.8/search"));

        Assert.Equal("<html>ok</html>", body);
        Assert.Contains("http://indexer-egress:8080", handler.RequestBody, StringComparison.Ordinal);
        Assert.Contains("\"maxTimeout\":60000", handler.RequestBody, StringComparison.Ordinal);
        Assert.Equal(FlareSolverrClient.EndpointUrl, handler.RequestUri);
    }

    [Fact]
    public async Task Rejects_private_target_cross_origin_final_url_and_oversized_json()
    {
        var privateHandler = new BrowserHandler("{}");
        var privateClient = new FlareSolverrClient(new HttpClient(privateHandler));
        await Assert.ThrowsAsync<HttpRequestException>(() => privateClient.FetchAsync(
            new Uri("http://127.0.0.1/"), new Uri("http://127.0.0.1/search")));
        Assert.Null(privateHandler.RequestUri);

        var crossOrigin = new FlareSolverrClient(new HttpClient(new BrowserHandler("""
            {"status":"ok","solution":{"status":200,"url":"https://1.1.1.1/search","response":"ok"}}
            """)));
        await Assert.ThrowsAsync<HttpRequestException>(() => crossOrigin.FetchAsync(
            new Uri("https://8.8.8.8/"), new Uri("https://8.8.8.8/search")));

        var oversized = new FlareSolverrClient(new HttpClient(new BrowserHandler(
            new string('x', FlareSolverrClient.MaxBodyBytes + 1))));
        await Assert.ThrowsAsync<InvalidDataException>(() => oversized.FetchAsync(
            new Uri("https://8.8.8.8/"), new Uri("https://8.8.8.8/search")));
    }

    [Fact]
    public async Task A_solved_clearance_keeps_only_the_challenge_cookies_for_the_site_and_the_browser_agent()
    {
        // The browser also holds the site's own session cookie from the page it loaded. That one is not
        // the clearance's to carry: the login sequence owns the session, and mixing a browser session in
        // would sign every search in as whoever that page happened to be.
        var handler = new BrowserHandler("""
            {"status":"ok","solution":{"status":200,"url":"https://8.8.8.8/","response":"<html>ok</html>",
             "userAgent":"Mozilla/5.0 Test",
             "cookies":[
               {"name":"cf_clearance","value":"solved","domain":".8.8.8.8","path":"/","expiry":1900000000},
               {"name":"__cf_bm","value":"bm","domain":"8.8.8.8","path":"/"},
               {"name":"PHPSESSID","value":"browser-session","domain":"8.8.8.8","path":"/"},
               {"name":"cf_clearance","value":"elsewhere","domain":"1.1.1.1","path":"/"}
             ]}}
            """);
        var client = new FlareSolverrClient(new HttpClient(handler));

        var clearance = await client.SolveAsync(new Uri("https://8.8.8.8/"));

        Assert.Equal("Mozilla/5.0 Test", clearance.UserAgent);
        Assert.Equal(["__cf_bm", "cf_clearance"], clearance.Cookies.Select(c => c.Name).Order());
        Assert.Equal("solved", Assert.Single(clearance.Cookies, c => c.Name == "cf_clearance").Value);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1900000000), clearance.ExpiresAt);
        Assert.Contains("http://indexer-egress:8080", handler.RequestBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_clearance_is_solved_at_the_page_that_was_challenged_not_at_the_site_root()
    {
        // A site can serve its front page freely and challenge only the pages behind it. Solved at the
        // root, the browser was never challenged, came back with no clearance cookie, and every
        // cleared request after it met the challenge again.
        var handler = new BrowserHandler("""
            {"status":"ok","solution":{"status":200,"url":"https://8.8.8.8/index.php?page=login","response":"ok",
             "userAgent":"Agent","cookies":[]}}
            """);
        var client = new FlareSolverrClient(new HttpClient(handler));

        await client.SolveAsync(new Uri("https://8.8.8.8/index.php?page=login"));

        Assert.Contains("\"url\":\"https://8.8.8.8/index.php?page=login\"", handler.RequestBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_clearance_whose_agent_could_inject_a_header_or_that_landed_elsewhere_is_refused()
    {
        var injected = new FlareSolverrClient(new HttpClient(new BrowserHandler("""
            {"status":"ok","solution":{"status":200,"url":"https://8.8.8.8/","response":"ok",
             "userAgent":"Agent\r\nX-Evil: 1","cookies":[]}}
            """)));
        await Assert.ThrowsAsync<HttpRequestException>(() => injected.SolveAsync(new Uri("https://8.8.8.8/")));

        var elsewhere = new FlareSolverrClient(new HttpClient(new BrowserHandler("""
            {"status":"ok","solution":{"status":200,"url":"https://1.1.1.1/","response":"ok",
             "userAgent":"Agent","cookies":[]}}
            """)));
        await Assert.ThrowsAsync<HttpRequestException>(() => elsewhere.SolveAsync(new Uri("https://8.8.8.8/")));
    }

    private sealed class BrowserHandler(string responseBody) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            };
        }
    }
}
