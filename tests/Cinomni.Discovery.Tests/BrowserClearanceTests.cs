using System.Net;
using System.Text;
using Cinomni.Discovery.Indexers;
using Cinomni.Discovery.Indexers.Definition;

namespace Cinomni.Discovery.Tests;

/// <summary>
/// The pieces that let a login-walled site behind a browser challenge be searched without a browser
/// per request: recognising the challenge, carrying a solved clearance on a plain request, and keeping
/// one clearance per indexer until the site refuses it.
/// </summary>
public sealed class BrowserClearanceTests
{
    private static readonly Uri Site = new("https://8.8.8.8/");

    [Fact]
    public void A_challenge_page_is_recognised_by_its_marker_or_its_header_but_never_on_a_success()
    {
        using var challenged = Response(HttpStatusCode.Forbidden);
        Assert.True(BrowserChallenge.IsChallenge(challenged, "<html><title>Just a moment...</title></html>"));

        using var flagged = Response(HttpStatusCode.ServiceUnavailable);
        flagged.Headers.TryAddWithoutValidation("cf-mitigated", "challenge");
        Assert.True(BrowserChallenge.IsChallenge(flagged, "<html></html>"));

        // A 200 that happens to quote the phrase is a page, and a plain 403 is the site's own refusal —
        // treating either as a challenge would re-solve for ever instead of reporting what happened.
        using var page = Response(HttpStatusCode.OK);
        Assert.False(BrowserChallenge.IsChallenge(page, "<p>Just a moment...</p>"));
        using var refused = Response(HttpStatusCode.Forbidden);
        Assert.False(BrowserChallenge.IsChallenge(refused, "<html>Access denied</html>"));
    }

    [Fact]
    public void Applying_a_clearance_sets_its_agent_and_lets_its_cookies_win_over_stale_session_copies()
    {
        var clearance = new BrowserClearance(
            [new JarCookie("cf_clearance", "fresh")], "Agent/1.0", DateTimeOffset.MaxValue);
        IReadOnlyList<JarCookie> session = [new JarCookie("uid", "7"), new JarCookie("cf_clearance", "stale")];
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Site, "search"));

        BrowserChallenge.Apply(request, clearance, session);

        Assert.Equal("Agent/1.0", string.Join(" ", request.Headers.UserAgent.Select(v => v.ToString())));
        var cookie = Assert.Single(request.Headers.GetValues("Cookie"));
        Assert.Contains("uid=7", cookie, StringComparison.Ordinal);
        Assert.Contains("cf_clearance=fresh", cookie, StringComparison.Ordinal);
        Assert.DoesNotContain("stale", cookie, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_cache_solves_once_reuses_the_clearance_and_solves_again_only_when_refused()
    {
        var solver = new CountingSolver();
        var cache = new IndexerClearanceCache(() => solver, new FixedClock(DateTimeOffset.UnixEpoch));
        var indexerId = Guid.NewGuid();

        var first = await cache.GetAsync(indexerId, Site);
        var second = await cache.GetAsync(indexerId, Site);
        Assert.Same(first, second);
        Assert.Equal(1, solver.Solves);

        cache.Invalidate(indexerId, first);
        var renewed = await cache.GetAsync(indexerId, Site);
        Assert.NotSame(first, renewed);
        Assert.Equal(2, solver.Solves);

        // A refusal reported against a clearance that has already been replaced says nothing about its
        // successor: a concurrent search renewed it, and dropping the new one would solve twice.
        cache.Invalidate(indexerId, first);
        Assert.Same(renewed, await cache.GetAsync(indexerId, Site));
        Assert.Equal(2, solver.Solves);
    }

    [Fact]
    public async Task The_cache_solves_again_once_the_clearance_has_expired()
    {
        var clock = new FixedClock(DateTimeOffset.UnixEpoch);
        var solver = new CountingSolver { Lifetime = TimeSpan.FromMinutes(10) };
        var cache = new IndexerClearanceCache(() => solver, clock);
        var indexerId = Guid.NewGuid();

        await cache.GetAsync(indexerId, Site);
        clock.Now = DateTimeOffset.UnixEpoch.AddMinutes(11);
        await cache.GetAsync(indexerId, Site);

        Assert.Equal(2, solver.Solves);
    }

    [Fact]
    public async Task A_clearance_the_site_says_has_already_expired_is_still_kept_until_the_site_refuses_it()
    {
        // The site sets the expiry. Honoured literally, an expiry in the past made every single request
        // open the browser again, and two browser slots shared by every indexer filled with solves.
        var clock = new FixedClock(DateTimeOffset.UnixEpoch.AddDays(1));
        var solver = new CountingSolver { Lifetime = TimeSpan.Zero };
        var cache = new IndexerClearanceCache(() => solver, clock);
        var indexerId = Guid.NewGuid();

        await cache.GetAsync(indexerId, Site);
        await cache.GetAsync(indexerId, Site);

        Assert.Equal(1, solver.Solves);
    }

    [Fact]
    public async Task A_failed_solve_fails_fast_for_a_while_instead_of_opening_the_browser_again()
    {
        var clock = new FixedClock(DateTimeOffset.UnixEpoch);
        var solver = new CountingSolver { Fails = true };
        var cache = new IndexerClearanceCache(() => solver, clock);
        var indexerId = Guid.NewGuid();

        await Assert.ThrowsAsync<HttpRequestException>(() => cache.GetAsync(indexerId, Site));
        await Assert.ThrowsAsync<HttpRequestException>(() => cache.GetAsync(indexerId, Site));
        Assert.Equal(1, solver.Solves);

        // Once the hold-off has passed the site is tried again.
        clock.Now += IndexerClearanceCache.FailedSolveHoldOff;
        solver.Fails = false;
        await cache.GetAsync(indexerId, Site);
        Assert.Equal(2, solver.Solves);
    }

    private static HttpResponseMessage Response(HttpStatusCode status) =>
        new(status) { Content = new StringContent(string.Empty, Encoding.UTF8, "text/html") };

    internal sealed class CountingSolver : IIndexerClearanceSolver
    {
        public int Solves { get; private set; }

        /// <summary>Measured from the Unix epoch, the fixed clock's start; unset, the clearance names no expiry.</summary>
        public TimeSpan? Lifetime { get; init; }

        public bool Fails { get; set; }

        public Task<BrowserClearance> SolveAsync(Uri baseUri, CancellationToken cancellationToken = default)
        {
            Solves++;
            if (Fails)
            {
                return Task.FromException<BrowserClearance>(new HttpRequestException("Browser request failed."));
            }

            return Task.FromResult(new BrowserClearance(
                [new JarCookie("cf_clearance", $"solved-{Solves}")],
                "Agent/1.0",
                Lifetime is { } lifetime ? DateTimeOffset.UnixEpoch + lifetime : DateTimeOffset.MaxValue));
        }
    }

    internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
