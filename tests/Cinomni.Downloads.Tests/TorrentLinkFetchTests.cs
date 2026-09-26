using System.Net;
using Cinomni.Downloads.Engine;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cinomni.Downloads.Tests;

/// <summary>
/// Fetching the .torrent behind an indexer link. The hardened client never follows a redirect on its
/// own, because the remote side chooses where one goes; this fetch follows them itself and checks every
/// destination as it would the first. A torrent cache moving from http to https, or an indexer whose
/// download link answers with a magnet, must still become a download.
/// </summary>
public sealed class TorrentLinkFetchTests
{
    private static readonly byte[] Torrent = "d4:infod4:name4:Filmee"u8.ToArray();

    [Fact]
    public async Task A_permanent_redirect_to_https_is_followed_to_the_torrent()
    {
        var web = new ScriptedWeb()
            .Redirect("http://cache.example/torrent/A.torrent", HttpStatusCode.MovedPermanently, "https://cache.example/torrent/A.torrent")
            .Serve("https://cache.example/torrent/A.torrent", Torrent);

        var fetched = await Fetch(web, "http://cache.example/torrent/A.torrent");

        Assert.Equal(Torrent, fetched.File);
        Assert.Null(fetched.Magnet);
    }

    [Fact]
    public async Task A_relative_location_is_resolved_against_the_page_that_sent_it()
    {
        var web = new ScriptedWeb()
            .Redirect("https://indexer.example/download?id=7", HttpStatusCode.Found, "/files/7.torrent")
            .Serve("https://indexer.example/files/7.torrent", Torrent);

        var fetched = await Fetch(web, "https://indexer.example/download?id=7");

        Assert.Equal(Torrent, fetched.File);
    }

    [Fact]
    public async Task A_redirect_to_a_magnet_is_handed_back_as_the_magnet()
    {
        const string Magnet = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=Film";
        var web = new ScriptedWeb()
            .Redirect("https://indexer.example/download?id=7", HttpStatusCode.Found, Magnet);

        var fetched = await Fetch(web, "https://indexer.example/download?id=7");

        Assert.Null(fetched.File);
        Assert.Equal(Magnet, fetched.Magnet);
        Assert.Single(web.Requested);
    }

    [Theory]
    [InlineData("http://192.168.1.1/admin")]
    [InlineData("http://127.0.0.1:5268/api/operations/settings")]
    [InlineData("file:///etc/passwd")]
    [InlineData("http://user:pass@cache.example/x.torrent")]
    public async Task A_redirect_to_a_destination_the_guard_refuses_is_never_requested(string location)
    {
        var web = new ScriptedWeb()
            .Redirect("https://indexer.example/download?id=7", HttpStatusCode.Found, location);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Fetch(web, "https://indexer.example/download?id=7"));

        Assert.Contains("redirected", failure.Message, StringComparison.Ordinal);
        Assert.Single(web.Requested);
    }

    [Fact]
    public async Task A_redirect_loop_gives_up_after_a_bounded_number_of_hops()
    {
        var web = new ScriptedWeb()
            .Redirect("https://a.example/t", HttpStatusCode.Found, "https://b.example/t")
            .Redirect("https://b.example/t", HttpStatusCode.Found, "https://a.example/t");

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => Fetch(web, "https://a.example/t"));

        Assert.Contains("too many redirects", failure.Message, StringComparison.Ordinal);
        Assert.Equal(TorrentLinkFetch.MaxRedirects + 1, web.Requested.Count);
    }

    [Fact]
    public async Task A_redirect_without_a_location_fails_as_such()
    {
        var web = new ScriptedWeb().Redirect("https://indexer.example/download", HttpStatusCode.MovedPermanently, null);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => Fetch(web, "https://indexer.example/download"));

        Assert.Contains("without saying where", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_failure_quotes_the_link_and_the_key_it_carries()
    {
        const string Key = "an-indexer-api-key";
        var web = new ScriptedWeb()
            .Redirect($"https://indexer.example/download?apikey={Key}", HttpStatusCode.Found, $"http://10.0.0.5/x?apikey={Key}");

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Fetch(web, $"https://indexer.example/download?apikey={Key}"));

        Assert.DoesNotContain(Key, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_link_the_guard_refuses_is_not_requested_at_all()
    {
        var web = new ScriptedWeb();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Fetch(web, "http://169.254.169.254/latest/meta-data"));

        Assert.Empty(web.Requested);
    }

    private static Task<FetchedTorrent> Fetch(ScriptedWeb web, string url)
    {
        using var http = new HttpClient(web);
        return TorrentLinkFetch.FetchAsync(http, url, NullLogger.Instance, CancellationToken.None);
    }

    /// <summary>Answers by exact URL, the way the hardened handler would: redirects are returned, not followed.</summary>
    private sealed class ScriptedWeb : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpResponseMessage>> _responses = new(StringComparer.Ordinal);

        public List<string> Requested { get; } = [];

        public ScriptedWeb Redirect(string url, HttpStatusCode status, string? location)
        {
            _responses[url] = () =>
            {
                var response = new HttpResponseMessage(status);
                if (location is not null)
                {
                    response.Headers.TryAddWithoutValidation("Location", location);
                }

                return response;
            };
            return this;
        }

        public ScriptedWeb Serve(string url, byte[] body)
        {
            _responses[url] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            Requested.Add(url);
            return Task.FromResult(_responses.TryGetValue(url, out var respond)
                ? respond()
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
