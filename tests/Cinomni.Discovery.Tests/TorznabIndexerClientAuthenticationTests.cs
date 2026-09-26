using System.Net;
using System.Text;
using Cinomni.Discovery.Contracts;
using Cinomni.Discovery.Indexers;
using Cinomni.Kernel.Identifiers;
using Cinomni.Search.Contracts;

namespace Cinomni.Discovery.Tests;

/// <summary>
/// How a Torznab/Newznab request authenticates: an API key rides in the query, a username and
/// password ride in an HTTP Basic header and only over https. No database — the transport is faked.
/// </summary>
public sealed class TorznabIndexerClientAuthenticationTests
{
    private static readonly SearchCriterion Criterion = new("Interstellar", 2014, null, null, "Movie");

    [Fact]
    public async Task A_username_and_password_are_sent_as_basic_auth_and_kept_out_of_the_url()
    {
        var handler = EmptyFeed();
        var client = new TorznabIndexerClient(new HttpClient(handler));

        await client.SearchAsync(
            Indexer("https://proxy.example/torznab"), new IndexerCredential("operator", "p:ss wörd"), null, Criterion);

        var authorization = Assert.Single(handler.Authorizations);
        Assert.NotNull(authorization);
        Assert.Equal("Basic", authorization.Scheme);
        Assert.Equal(
            "operator:p:ss wörd",
            Encoding.UTF8.GetString(Convert.FromBase64String(authorization.Parameter!)));
        var url = Assert.Single(handler.Requests).ToString();
        Assert.DoesNotContain("operator", url, StringComparison.Ordinal);
        Assert.DoesNotContain("apikey", url, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_api_key_sends_no_authorization_header()
    {
        var handler = EmptyFeed();
        var client = new TorznabIndexerClient(new HttpClient(handler));

        await client.SearchAsync(Indexer("https://idx.example/api"), new IndexerCredential(null, "k3y"), null, Criterion);

        Assert.Null(Assert.Single(handler.Authorizations));
        Assert.Contains("apikey=k3y", Assert.Single(handler.Requests).ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_password_is_never_sent_over_plain_http()
    {
        var handler = EmptyFeed();
        var client = new TorznabIndexerClient(new HttpClient(handler));

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SearchAsync(
            Indexer("http://idx.example/api"), new IndexerCredential("operator", "s3cr3t"), null, Criterion));

        // Refused before the wire: not one request carried the password anywhere.
        Assert.Empty(handler.Requests);
    }

    private static FakeHttpMessageHandler EmptyFeed() =>
        new(HttpStatusCode.OK, "<rss><channel></channel></rss>", "application/xml");

    private static IndexerSummary Indexer(string baseUrl) =>
        new(new IndexerId(Guid.NewGuid()), "Alpha", IndexerProtocol.Torznab, baseUrl, 1, true);
}
