using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cinomni.Discovery.Indexers.Definition;
using Cinomni.Kernel.Net;

namespace Cinomni.Discovery.Indexers;

internal interface IIndexerBrowserTransport
{
    Task<string> FetchAsync(Uri baseUri, Uri targetUri, CancellationToken cancellationToken = default);
}

/// <summary>Fixed-endpoint browser boundary for sites that explicitly require FlareSolverr.</summary>
internal sealed class FlareSolverrClient(HttpClient httpClient) : IIndexerBrowserTransport, IIndexerClearanceSolver
{
    /// <summary>A user agent is one header line; anything longer is not a browser's.</summary>
    internal const int MaxUserAgentLength = 512;

    /// <summary>More challenge cookies than any provider sets; a bound on what a solve can hand back.</summary>
    internal const int MaxClearanceCookies = 16;

    internal const int MaxCookieValueLength = 4096;

    private static readonly SemaphoreSlim BrowserSlots = new(2, 2);
    internal static readonly Uri ControlUrl = new("http://flaresolverr:8191/");
    internal static readonly Uri EndpointUrl = new(ControlUrl, "v1");
    internal const string ProxyUrl = "http://indexer-egress:8080";
    internal const int MaxBodyBytes = 8 * 1024 * 1024;
    /// <summary>How long FlareSolverr may spend solving a challenge (its own default, and <c>maxTimeout</c>).</summary>
    internal static readonly TimeSpan ChallengeTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long this client waits for FlareSolverr's answer: the solve plus room to send the page back.
    /// The two used to be the same 30 seconds, so a challenge that took its whole allowance was always
    /// cancelled here just before FlareSolverr could report it solved.
    /// </summary>
    internal static readonly TimeSpan RequestTimeout = ChallengeTimeout + TimeSpan.FromSeconds(15);

    public async Task<string> FetchAsync(
        Uri baseUri, Uri targetUri, CancellationToken cancellationToken = default) =>
        (await InBrowserSlotAsync(baseUri, targetUri, cancellationToken)).Response ?? string.Empty;

    /// <summary>
    /// Visits <paramref name="target"/> in the browser so its challenge is solved, and keeps what the
    /// solve left: the challenge-family cookies scoped to this host, and the agent that earned them.
    /// The target is the page that was challenged, never the site root: a site may serve its front
    /// page freely and challenge only what is behind it, and a browser that was never challenged comes
    /// back with no clearance at all. The page's own cookies (a session id the visit happened to be
    /// issued) are dropped — the login sequence owns the session, never a browser that wandered past it.
    /// </summary>
    public async Task<BrowserClearance> SolveAsync(Uri target, CancellationToken cancellationToken = default)
    {
        var root = new Uri(target.GetLeftPart(UriPartial.Authority) + "/");
        var solution = await InBrowserSlotAsync(root, target, cancellationToken);

        var userAgent = solution.UserAgent ?? string.Empty;
        if (userAgent.Length is 0 or > MaxUserAgentLength || userAgent.Any(char.IsControl))
        {
            throw new HttpRequestException("Browser clearance carried an unusable user agent.");
        }

        var host = root.DnsSafeHost;
        var cookies = (solution.Cookies ?? [])
            .Where(cookie => cookie.Name is { Length: > 0 } name
                && BrowserChallenge.IsClearanceCookie(name)
                && cookie.Value is { Length: <= MaxCookieValueLength } value
                && !name.Any(IsNotTokenChar) && !value.Any(IsNotCookieValueChar)
                && DomainMatches(cookie.Domain, host))
            .Take(MaxClearanceCookies)
            .ToList();

        // Outside a plausible range (the browser's own ceiling is decades), an expiry is ignored rather
        // than trusted: an absurd value is otherwise an overflow, not a date.
        var expiresAt = cookies
            .Where(cookie => cookie.Name == "cf_clearance" && cookie.Expiry is > 0 and < MaxExpirySeconds)
            .Select(cookie => DateTimeOffset.FromUnixTimeSeconds((long)cookie.Expiry!.Value))
            .DefaultIfEmpty(DateTimeOffset.MaxValue)
            .Min();

        return new BrowserClearance(
            cookies.Select(cookie => new JarCookie(cookie.Name!, cookie.Value!, cookie.Path)).ToList(),
            userAgent,
            expiresAt);
    }

    /// <summary>Year 9999 in Unix seconds: the last instant a <see cref="DateTimeOffset"/> can hold.</summary>
    private const double MaxExpirySeconds = 253_402_300_799;

    /// <summary>
    /// A cookie set for the host itself or for a parent domain of it, never a sibling's — and never
    /// for a bare suffix such as <c>com</c>, which would scope it to every site under that suffix.
    /// </summary>
    private static bool DomainMatches(string? domain, string host)
    {
        var bare = (domain ?? string.Empty).TrimStart('.');
        return bare.Length > 0
            && (string.Equals(bare, host, StringComparison.OrdinalIgnoreCase)
                || (bare.Contains('.') && host.EndsWith("." + bare, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool IsNotTokenChar(char c) => c <= ' ' || c >= 0x7F || "()<>@,;:\\\"/[]?={}".Contains(c);

    private static bool IsNotCookieValueChar(char c) => c < '!' || c >= 0x7F || c is '"' or ',' or ';' or '\\';

    private async Task<Solution> InBrowserSlotAsync(
        Uri baseUri, Uri targetUri, CancellationToken cancellationToken)
    {
        // Bounded: a slot is held at most RequestTimeout, so waiting longer than that means both are
        // stuck, and this search should fail rather than queue behind them for as long as they take.
        if (!await BrowserSlots.WaitAsync(RequestTimeout, cancellationToken))
        {
            throw new HttpRequestException("The browser transport is busy.");
        }

        try
        {
            return await FetchCoreAsync(baseUri, targetUri, cancellationToken);
        }
        catch (OperationCanceledException timedOut) when (!cancellationToken.IsCancellationRequested)
        {
            // This client's own deadline, not the caller's cancellation. As a cancellation it escaped
            // every "one indexer's failure is that indexer's" catch and ended the whole search; as the
            // failed request it is, it costs one row or one indexer and nothing more.
            throw new HttpRequestException("The browser request timed out.", timedOut);
        }
        finally
        {
            BrowserSlots.Release();
        }
    }

    private async Task<Solution> FetchCoreAsync(
        Uri baseUri, Uri targetUri, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        var requestToken = timeout.Token;
        await ValidateTargetAsync(baseUri, targetUri, requestToken);

        using var response = await httpClient.PostAsJsonAsync(EndpointUrl, new
        {
            cmd = "request.get",
            url = targetUri.ToString(),
            maxTimeout = (int)ChallengeTimeout.TotalMilliseconds,
            proxy = new { url = ProxyUrl },
        }, requestToken);
        response.EnsureSuccessStatusCode();

        var bytes = await response.Content.ReadAsByteArrayAsync(requestToken);
        if (bytes.Length > MaxBodyBytes)
        {
            throw new InvalidDataException("Browser response exceeded the allowed size.");
        }

        var envelope = JsonSerializer.Deserialize<ResponseEnvelope>(bytes, JsonOptions)
            ?? throw new InvalidDataException("Browser response was invalid.");
        if (!string.Equals(envelope.Status, "ok", StringComparison.OrdinalIgnoreCase)
            || envelope.Solution is not { Status: >= 200 and <= 299 } solution
            || !Uri.TryCreate(solution.Url, UriKind.Absolute, out var finalUri))
        {
            throw new HttpRequestException("Browser request failed.");
        }

        await ValidateTargetAsync(baseUri, finalUri, requestToken);
        if (System.Text.Encoding.UTF8.GetByteCount(solution.Response ?? string.Empty) > MaxBodyBytes)
        {
            throw new InvalidDataException("Browser page exceeded the allowed size.");
        }

        return solution;
    }

    private static async Task ValidateTargetAsync(Uri baseUri, Uri targetUri, CancellationToken cancellationToken)
    {
        if (!SsrfGuard.TryValidatePublicUrl(targetUri.ToString(), out _)
            || !DefinitionQueryBuilder.SameOrigin(baseUri, targetUri))
        {
            throw new HttpRequestException("Browser target was rejected.");
        }

        var addresses = await Dns.GetHostAddressesAsync(targetUri.DnsSafeHost, cancellationToken);
        if (addresses.Length == 0 || addresses.Any(SsrfGuard.IsBlockedAddress))
        {
            // The same refusal the direct transport raises, so a site that resolves to a private or
            // loopback address (an ISP's DNS block, typically) is diagnosed the same way on both paths.
            throw new HttpRequestException(
                "Browser target was rejected.",
                new IOException($"Refusing to connect to '{targetUri.DnsSafeHost}': no public address."));
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 16,
    };

    private sealed record ResponseEnvelope(string? Status, Solution? Solution);

    private sealed record Solution(
        int Status, string? Url, string? Response, string? UserAgent = null, IReadOnlyList<SolverCookie>? Cookies = null);

    /// <summary>A cookie as the browser reports it; <c>expiry</c> is Unix seconds when present.</summary>
    private sealed record SolverCookie(string? Name, string? Value, string? Domain, string? Path, double? Expiry);
}
