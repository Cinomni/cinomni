using System.Collections.Concurrent;
using System.Net;
using Cinomni.Discovery.Indexers.Definition;

namespace Cinomni.Discovery.Indexers;

/// <summary>
/// What a browser challenge leaves behind once it is solved: the challenge-family cookies and the
/// user agent that solved it. The site binds the cookies to that agent and to the address the solve
/// came from, so a plain request carrying both — sent out through the same egress proxy the browser
/// used — is let through without a browser of its own. That is what makes a login, a search and a
/// .torrent download possible on a site the browser transport alone can only read pages from.
/// </summary>
/// <param name="ExpiresAt">When the clearance cookie says it lapses; <see cref="DateTimeOffset.MaxValue"/> when it does not say.</param>
internal sealed record BrowserClearance(IReadOnlyList<JarCookie> Cookies, string UserAgent, DateTimeOffset ExpiresAt);

/// <summary>Solves a site's browser challenge once and hands back the clearance it produced.</summary>
internal interface IIndexerClearanceSolver
{
    Task<BrowserClearance> SolveAsync(Uri target, CancellationToken cancellationToken = default);
}

/// <summary>One clearance per indexer, solved on first use and again only when the site refuses it.</summary>
internal interface IIndexerClearance
{
    Task<BrowserClearance> GetAsync(Guid indexerId, Uri solveAt, CancellationToken cancellationToken = default);

    /// <summary>
    /// The site answered <paramref name="stale"/> with a challenge. Ignored when it has already been
    /// replaced: a concurrent request renewed it, and the renewal says nothing about its successor.
    /// </summary>
    void Invalidate(Guid indexerId, BrowserClearance stale);
}

/// <summary>A cleared request met a challenge again, even after its clearance was renewed.</summary>
internal sealed class IndexerChallengeException(string message) : HttpRequestException(message);

/// <summary>Recognising a challenge, and carrying a clearance on a request.</summary>
internal static class BrowserChallenge
{
    /// <summary>The challenge families a clearance carries. Everything else a browser holds is the site's own.</summary>
    private static readonly string[] ClearancePrefixes = ["cf_", "__cf", "_cf"];

    /// <summary>
    /// A challenge is a refusal (403/503) that says so: the provider's mitigation header, or the
    /// interstitial page itself. A plain 403 is the site's own answer and is left for the caller to
    /// judge; a 200 that quotes the phrase is a page.
    /// </summary>
    public static bool IsChallenge(HttpResponseMessage response, string body)
    {
        if (response.StatusCode is not (HttpStatusCode.Forbidden or HttpStatusCode.ServiceUnavailable))
        {
            return false;
        }

        if (response.Headers.TryGetValues("cf-mitigated", out var mitigated)
            && mitigated.Any(value => value.Equals("challenge", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return body.Contains("_cf_chl_opt", StringComparison.Ordinal)
            || body.Contains("<title>Just a moment...</title>", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsClearanceCookie(string name) =>
        ClearancePrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Sends the request as the browser that solved the challenge: its agent, and its clearance
    /// cookies next to the session's own. The clearance wins a name clash, because a session jar can
    /// hold a stale copy of a challenge cookie the site refreshed on a response.
    /// </summary>
    public static void Apply(HttpRequestMessage request, BrowserClearance clearance, IReadOnlyList<JarCookie>? session)
    {
        request.Headers.UserAgent.Clear();
        request.Headers.TryAddWithoutValidation("User-Agent", clearance.UserAgent);

        var clearanceNames = clearance.Cookies.Select(cookie => cookie.Name).ToHashSet(StringComparer.Ordinal);
        IReadOnlyList<JarCookie> jar = (session ?? [])
            .Where(cookie => !clearanceNames.Contains(cookie.Name))
            .Concat(clearance.Cookies)
            .ToList();
        request.Headers.Remove("Cookie");
        if (IndexerSessionCookies.HeaderFor(jar, request.RequestUri!) is { } header)
        {
            request.Headers.TryAddWithoutValidation("Cookie", header);
        }
    }

    /// <summary>The session's cookies without any challenge cookie a response happened to set.</summary>
    public static IReadOnlyList<JarCookie> WithoutClearance(IReadOnlyList<JarCookie> jar) =>
        jar.Where(cookie => !IsClearanceCookie(cookie.Name)).ToList();
}

/// <summary>
/// Keeps one clearance per indexer in memory. Deliberately never persisted: a solve costs one browser
/// visit, and a clearance written to disk would be a credential-grade cookie at rest for no gain.
/// One solve runs at a time per indexer, so concurrent searches share it instead of each opening a
/// browser against the same site.
/// </summary>
internal sealed class IndexerClearanceCache(Func<IIndexerClearanceSolver> solverFactory, TimeProvider time)
    : IIndexerClearance
{
    /// <summary>The proxied client every cleared request goes out on — the address the clearance is bound to.</summary>
    internal const string HttpClientName = "IndexerClearance";

    /// <summary>
    /// The longest a clearance is trusted without the site refusing it first. Shorter than most
    /// providers grant, so a clearance nobody refused is still renewed before it can quietly lapse
    /// in the middle of a login sequence.
    /// </summary>
    internal static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(30);

    /// <summary>Renewed this long before its own expiry, so no request sets out on a clearance about to lapse.</summary>
    private static readonly TimeSpan ExpiryMargin = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The shortest a clearance is kept, whatever expiry the site stamps on it. The site chooses that
    /// expiry; honoured literally, one in the past would open the browser for every request and fill
    /// the two browser slots every indexer shares. A clearance that really lapsed is refused by the
    /// site, and the refusal renews it — so keeping it longer costs one retry, never a wrong answer.
    /// </summary>
    internal static readonly TimeSpan MinLifetime = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long a failed solve fails fast. A site that cannot be solved now will not be solved by the
    /// next request a second later either, and every attempt holds a browser slot for up to a minute.
    /// </summary>
    internal static readonly TimeSpan FailedSolveHoldOff = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _gates = new();
    private readonly ConcurrentDictionary<Guid, Kept> _kept = new();
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _failedAt = new();

    private sealed record Kept(BrowserClearance Clearance, DateTimeOffset UsableUntil);

    public async Task<BrowserClearance> GetAsync(Guid indexerId, Uri solveAt, CancellationToken cancellationToken = default)
    {
        if (TryUsable(indexerId) is { } usable)
        {
            return usable;
        }

        var gate = _gates.GetOrAdd(indexerId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            // Another caller may have solved it while this one waited for the gate.
            if (TryUsable(indexerId) is { } solvedMeanwhile)
            {
                return solvedMeanwhile;
            }

            if (_failedAt.TryGetValue(indexerId, out var failedAt) && time.GetUtcNow() < failedAt + FailedSolveHoldOff)
            {
                throw new HttpRequestException("The site's browser challenge could not be solved recently; not trying again yet.");
            }

            BrowserClearance solved;
            try
            {
                solved = await solverFactory().SolveAsync(solveAt, cancellationToken);
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                _failedAt[indexerId] = time.GetUtcNow();
                throw;
            }

            _failedAt.TryRemove(indexerId, out _);
            var now = time.GetUtcNow();
            var ceiling = now + MaxAge;
            var floor = now + MinLifetime;
            var byExpiry = (solved.ExpiresAt < ceiling ? solved.ExpiresAt : ceiling) - ExpiryMargin;
            _kept[indexerId] = new Kept(solved, byExpiry > floor ? byExpiry : floor);
            return solved;
        }
        finally
        {
            gate.Release();
        }
    }

    public void Invalidate(Guid indexerId, BrowserClearance stale)
    {
        if (_kept.TryGetValue(indexerId, out var kept) && ReferenceEquals(kept.Clearance, stale))
        {
            _kept.TryRemove(new KeyValuePair<Guid, Kept>(indexerId, kept));
        }
    }

    private BrowserClearance? TryUsable(Guid indexerId) =>
        _kept.TryGetValue(indexerId, out var kept) && time.GetUtcNow() < kept.UsableUntil ? kept.Clearance : null;
}
