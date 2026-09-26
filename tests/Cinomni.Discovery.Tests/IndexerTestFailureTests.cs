using System.Net;
using System.Net.Sockets;
using Cinomni.Discovery.Application;
using Cinomni.Discovery.Indexers;

namespace Cinomni.Discovery.Tests;

public sealed class IndexerTestFailureTests
{
    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "HTTP 403")]
    [InlineData(HttpStatusCode.NotFound, "HTTP 404")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "HTTP 503")]
    public void Describe_names_the_status_a_site_answered_with(HttpStatusCode status, string expected)
    {
        var (code, message) = IndexerTestFailure.Describe(new HttpRequestException("ignored", null, status));

        Assert.Equal(IndexerTestFailure.HttpStatus, code);
        Assert.Contains(expected, message);
    }

    [Fact]
    public void Describe_explains_a_destination_the_guard_refused()
    {
        var refused = new HttpRequestException(
            "connect failed", new IOException("Refusing to connect to 'site.example': no public address."));

        var (code, message) = IndexerTestFailure.Describe(refused);

        Assert.Equal(IndexerTestFailure.DestinationRefused, code);
        // The host is for the operator's log, never for the API answer.
        Assert.DoesNotContain("site.example", message);
    }

    [Fact]
    public void Describe_gives_the_browser_path_the_same_refusal_as_the_direct_one()
    {
        // FlareSolverrClient's pre-flight check, when the name resolves to 127.0.0.1 (an ISP DNS block).
        var refused = new HttpRequestException(
            "Browser target was rejected.", new IOException("Refusing to connect to 'indexer.example': no public address."));

        Assert.Equal(IndexerTestFailure.DestinationRefused, IndexerTestFailure.Describe(refused).Code);
    }

    [Fact]
    public void Describe_tells_an_unresolvable_name_from_a_refused_connection()
    {
        var unresolved = new HttpRequestException("x", new SocketException((int)SocketError.HostNotFound));
        var refused = new HttpRequestException("x", new SocketException((int)SocketError.ConnectionRefused));

        Assert.Equal(IndexerTestFailure.Unresolved, IndexerTestFailure.Describe(unresolved).Code);
        Assert.Equal(IndexerTestFailure.Unreachable, IndexerTestFailure.Describe(refused).Code);
    }

    [Fact]
    public void Describe_reports_a_client_timeout_as_a_timeout()
    {
        var timeout = new TaskCanceledException("timed out", new TimeoutException());

        Assert.Equal(IndexerTestFailure.TimedOut, IndexerTestFailure.Describe(timeout).Code);
    }

    [Fact]
    public void Describe_never_repeats_what_the_remote_side_sent()
    {
        var hostile = new InvalidOperationException("<script>alert(1)</script> from the site");

        var (code, message) = IndexerTestFailure.Describe(hostile);

        Assert.Equal(IndexerTestFailure.Failed, code);
        Assert.DoesNotContain("script", message);
    }

    [Fact]
    public void Describe_points_a_feed_error_at_the_url_and_key()
    {
        var (code, _) = IndexerTestFailure.Describe(new TorznabFeedException("Example", TorznabFeedException.Unreadable));

        Assert.Equal(IndexerTestFailure.NotAFeed, code);
    }
}
