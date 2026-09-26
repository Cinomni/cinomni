using System.Text;
using Cinomni.Kernel.Net;
using Cinomni.Kernel.Results;
using Microsoft.Extensions.Logging;

namespace Cinomni.Discovery.Application;

/// <summary>
/// Fetches a catalog source's manifest. Registered separately so tests can replace the network with
/// a fake; the production implementation is <see cref="HttpIndexerCatalogFetcher"/>.
/// </summary>
public interface IIndexerCatalogFetcher
{
    /// <summary>The manifest body as text, or a named <c>discovery.catalog_source.*</c> error.</summary>
    Task<Result<string>> FetchAsync(Uri url, CancellationToken cancellationToken);
}

/// <summary>
/// The production fetcher: one GET over the Kernel's SSRF-safe handler (resolved address checked at
/// connect time, redirects never followed, no ambient proxy), a bounded time, and a body read with a
/// hard byte cap rather than trusting <c>Content-Length</c>. Failures come back as a fixed sentence
/// and a status code; the exception, which names the host, goes to the operator's log only.
/// </summary>
internal sealed class HttpIndexerCatalogFetcher(
    IHttpClientFactory httpClientFactory,
    ILogger<HttpIndexerCatalogFetcher> logger)
    : IIndexerCatalogFetcher
{
    public const string HttpClientName = "discovery.catalog-source";

    /// <summary>Generous for hundreds of hand-written definitions, small enough never to matter to memory.</summary>
    public const int MaxManifestBytes = 4 * 1024 * 1024;

    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    public const string FetchFailedCode = "discovery.catalog_source.fetch_failed";

    public const string TooLargeCode = "discovery.catalog_source.too_large";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public async Task<Result<string>> FetchAsync(Uri url, CancellationToken cancellationToken)
    {
        // Re-checked here, not only where the URL was accepted: this is the last point before a request.
        if (url.Scheme != Uri.UriSchemeHttps || !SsrfGuard.TryValidatePublicUrl(url.ToString(), out _))
        {
            return Fail(IndexerCatalogSources.InvalidUrlCode, "The source URL must be an https URL with a public host.");
        }

        var client = httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("application/json");
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                return Fail(FetchFailedCode,
                    $"The source answered HTTP {(int)response.StatusCode}; redirects are not followed, use the final URL.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return Fail(FetchFailedCode, $"The source answered HTTP {(int)response.StatusCode}.");
            }

            if (response.Content.Headers.ContentLength > MaxManifestBytes)
            {
                return TooLarge();
            }

            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            var bytes = await ReadBoundedAsync(body, cancellationToken);
            if (bytes is null)
            {
                return TooLarge();
            }

            return Result<string>.Success(StrictUtf8.GetString(bytes));
        }
        catch (DecoderFallbackException)
        {
            return Fail(IndexerCatalogManifest.InvalidManifestCode, "The manifest is not valid UTF-8 text.");
        }
        // The caller leaving is not an outcome to record; an HttpClient timeout, with the caller's token
        // untouched, is.
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Fail(FetchFailedCode, $"The source did not answer within {RequestTimeout.TotalSeconds:0} seconds.");
        }
        catch (Exception failure) when (failure is HttpRequestException or IOException)
        {
            logger.LogWarning(failure, "Fetching an indexer catalog source failed");
            return Fail(FetchFailedCode,
                "The source could not be reached. Its host must resolve to a public address and serve https.");
        }
    }

    /// <summary>Reads at most <see cref="MaxManifestBytes"/>; null when the body is longer.</summary>
    private static async Task<byte[]?> ReadBoundedAsync(Stream body, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxManifestBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static Result<string> TooLarge() => Fail(TooLargeCode,
        $"The manifest is larger than {MaxManifestBytes / (1024 * 1024)} MiB.");

    private static Result<string> Fail(string code, string message) => Result<string>.Failure(new Error(code, message));
}
