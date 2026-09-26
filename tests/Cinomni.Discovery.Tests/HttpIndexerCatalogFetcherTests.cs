using System.Net;
using Cinomni.Discovery.Application;
using Cinomni.Kernel.Diagnostics;
using Cinomni.Kernel.Net;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cinomni.Discovery.Tests;

/// <summary>
/// The production manifest fetcher at its transport boundary: bounded size (declared and actual),
/// no redirects, no plain http, and the Kernel's SSRF-safe handler refusing a host that resolves to
/// a private address.
/// </summary>
public sealed class HttpIndexerCatalogFetcherTests
{
    private static readonly Uri Source = new(CatalogManifests.SourceUrl);

    [Fact]
    public async Task A_manifest_within_the_cap_is_returned_verbatim()
    {
        var body = CatalogManifests.Manifest(CatalogManifests.Entry("x"));
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, body, "application/json");

        var fetched = await Fetcher(handler).FetchAsync(Source, CancellationToken.None);

        Assert.True(fetched.IsSuccess);
        Assert.Equal(body, fetched.Value);
        Assert.Equal(Source, Assert.Single(handler.Requests));
    }

    [Fact]
    public async Task A_declared_length_over_the_cap_is_refused_before_reading()
    {
        var handler = new StreamingHandler(() => new ByteArrayContent(new byte[16])
        {
            Headers = { ContentLength = HttpIndexerCatalogFetcher.MaxManifestBytes + 1L },
        });

        var fetched = await Fetcher(handler).FetchAsync(Source, CancellationToken.None);

        Assert.Equal(HttpIndexerCatalogFetcher.TooLargeCode, fetched.Error.Code);
    }

    [Fact]
    public async Task An_undeclared_body_over_the_cap_is_cut_off_while_reading()
    {
        // No Content-Length: only counting what arrives catches it.
        var handler = new StreamingHandler(() => new StreamContent(
            new EndlessStream(HttpIndexerCatalogFetcher.MaxManifestBytes + 1)));

        var fetched = await Fetcher(handler).FetchAsync(Source, CancellationToken.None);

        Assert.Equal(HttpIndexerCatalogFetcher.TooLargeCode, fetched.Error.Code);
    }

    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task A_non_success_answer_is_a_fetch_failure_and_no_redirect_is_followed(HttpStatusCode status)
    {
        var handler = new FakeHttpMessageHandler(status, "<html>ignored</html>");

        var fetched = await Fetcher(handler).FetchAsync(Source, CancellationToken.None);

        Assert.Equal(HttpIndexerCatalogFetcher.FetchFailedCode, fetched.Error.Code);
        Assert.Contains(((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture), fetched.Error.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ignored", fetched.Error.Message, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("http://catalog.example.org/indexers.json")]
    [InlineData("https://127.0.0.1/indexers.json")]
    [InlineData("https://192.168.1.10/indexers.json")]
    public async Task A_url_that_is_not_public_https_is_never_requested(string url)
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, "{}");

        var fetched = await Fetcher(handler).FetchAsync(new Uri(url), CancellationToken.None);

        Assert.Equal(IndexerCatalogSources.InvalidUrlCode, fetched.Error.Code);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_body_that_is_not_utf8_is_an_invalid_manifest()
    {
        var handler = new StreamingHandler(() => new ByteArrayContent([0x7B, 0xC3, 0x28, 0x7D]));

        var fetched = await Fetcher(handler).FetchAsync(Source, CancellationToken.None);

        Assert.Equal(IndexerCatalogManifest.InvalidManifestCode, fetched.Error.Code);
    }

    [Fact]
    public async Task A_host_name_resolving_to_loopback_is_refused_at_connect_by_the_ssrf_handler()
    {
        // The real Kernel handler, no fake: 'localhost' passes URL validation as a name and is refused
        // once resolved, which is the anti-rebinding half of the guard.
        using var handler = SsrfSafeHttpHandler.Create(TimeSpan.FromSeconds(5), CinomniTelemetry.Modules.Discovery);

        var fetched = await Fetcher(handler).FetchAsync(new Uri("https://localhost:9/indexers.json"), CancellationToken.None);

        Assert.Equal(HttpIndexerCatalogFetcher.FetchFailedCode, fetched.Error.Code);
        Assert.DoesNotContain("localhost", fetched.Error.Message, StringComparison.Ordinal);
    }

    private static HttpIndexerCatalogFetcher Fetcher(HttpMessageHandler handler) =>
        new(new SingleClientFactory(handler), NullLogger<HttpIndexerCatalogFetcher>.Instance);

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
        {
            Timeout = HttpIndexerCatalogFetcher.RequestTimeout,
        };
    }

    private sealed class StreamingHandler(Func<HttpContent> content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content() });
    }

    /// <summary>A body of <paramref name="length"/> bytes that is never buffered whole and declares no length.</summary>
    private sealed class EndlessStream(long length) : Stream
    {
        private long position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var remaining = (int)Math.Min(count, length - position);
            if (remaining <= 0)
            {
                return 0;
            }

            Array.Fill(buffer, (byte)' ', offset, remaining);
            position += remaining;
            return remaining;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
