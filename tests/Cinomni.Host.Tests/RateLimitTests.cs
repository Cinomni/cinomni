using System.Net;
using Cinomni.Host.RateLimiting;
using Cinomni.Kernel.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;

namespace Cinomni.Host.Tests;

/// <summary>
/// The throttle on the anonymous credential routes. These are abuse cases rather than feature tests:
/// every one of them describes something an unauthenticated caller would otherwise be able to do to
/// this installation.
/// </summary>
[Trait("Category", "RequiresDatabase")]
public sealed class RateLimitTests
{
    private const string Database = "cinomni_test_host_rate_limit";

    /// <summary>Three permits a minute, so the scenarios are short; the shape is the same at ten.</summary>
    private static IConfiguration ThreePerMinute() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Security:AnonymousRateLimit:Permits"] = "3",
            ["Security:AnonymousRateLimit:Window"] = "00:01:00",
        })
        .Build();

    [Fact]
    public async Task Login_refuses_a_client_that_exceeds_the_window_and_says_when_to_return()
    {
        await using var app = await RateLimitHttpTestHost.StartAsync(Database, ThreePerMinute());

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var allowed = await app.AttemptLoginAsync("203.0.113.10");
            Assert.Equal(StatusCodes.Status401Unauthorized, allowed.Response.StatusCode);
        }

        var refused = await app.AttemptLoginAsync("203.0.113.10");

        // Wrong credentials answer 401 all day; what changes at the limit is that the request is not
        // processed at all, which is the point — every attempt costs a full Argon2id verification even
        // for a username that does not exist.
        Assert.Equal(StatusCodes.Status429TooManyRequests, refused.Response.StatusCode);
        var retryAfter = Assert.Single(refused.Response.Headers.RetryAfter.ToArray());
        Assert.InRange(int.Parse(retryAfter!), 1, 60);
    }

    [Fact]
    public async Task One_client_hitting_the_limit_does_not_lock_out_another()
    {
        await using var app = await RateLimitHttpTestHost.StartAsync($"{Database}_partitioned", ThreePerMinute());

        for (var attempt = 0; attempt < 4; attempt++)
        {
            await app.AttemptLoginAsync("203.0.113.20");
        }

        // The whole reason the partition key is the client address. A limiter that counted every
        // request together would turn one attacker into an outage for the household, which is a worse
        // outcome than the attack: the attacker shares that bucket and is no more restricted than
        // anybody else, while everyone else is locked out.
        var other = await app.AttemptLoginAsync("203.0.113.21");

        Assert.Equal(StatusCodes.Status401Unauthorized, other.Response.StatusCode);
    }

    [Fact]
    public async Task A_tightened_limit_takes_effect_without_a_restart()
    {
        // Two permits configured, and the third attempt from the same address is refused. The limits
        // are part of the partition key precisely so that a change reaches a client already being
        // counted, rather than waiting for their partition to go idle.
        var tighter = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:AnonymousRateLimit:Permits"] = "2",
                ["Security:AnonymousRateLimit:Window"] = "00:01:00",
            })
            .Build();

        await using var app = await RateLimitHttpTestHost.StartAsync($"{Database}_tightened", tighter);

        await app.AttemptLoginAsync("203.0.113.30");
        await app.AttemptLoginAsync("203.0.113.30");
        var refused = await app.AttemptLoginAsync("203.0.113.30");

        Assert.Equal(StatusCodes.Status429TooManyRequests, refused.Response.StatusCode);
    }

    [Fact]
    public async Task Two_addresses_in_one_ipv6_prefix_share_a_budget()
    {
        await using var app = await RateLimitHttpTestHost.StartAsync($"{Database}_v6", ThreePerMinute());

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await app.AttemptLoginAsync("2001:db8:1:2::1");
        }

        // A single host is routed a /64 at minimum, so counting IPv6 per address would hand one
        // attacker 2^64 budgets and every request would be permitted. Counting the prefix is what
        // makes an IPv6 client cost what an IPv4 client costs.
        var sameNetwork = await app.AttemptLoginAsync("2001:db8:1:2::dead:beef");
        Assert.Equal(StatusCodes.Status429TooManyRequests, sameNetwork.Response.StatusCode);

        // A different /64 is a different client, and is not caught by their neighbour.
        var elsewhere = await app.AttemptLoginAsync("2001:db8:1:3::1");
        Assert.Equal(StatusCodes.Status401Unauthorized, elsewhere.Response.StatusCode);
    }

    [Fact]
    public async Task A_forwarded_header_is_ignored_when_no_proxy_is_declared()
    {
        await using var app = await RateLimitHttpTestHost.StartAsync($"{Database}_spoof", ThreePerMinute());

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await app.AttemptLoginAsync("203.0.113.9", forwardedFor: $"198.51.100.{attempt}");
        }

        // Nothing is declared, so the header is not evidence of anything: the connection address is
        // what counts, and rotating a header an attacker writes buys no extra budget.
        var spoofed = await app.AttemptLoginAsync("203.0.113.9", forwardedFor: "198.51.100.99");

        Assert.Equal(StatusCodes.Status429TooManyRequests, spoofed.Response.StatusCode);
    }

    [Fact]
    public async Task A_forwarded_header_from_a_declared_proxy_identifies_the_real_client()
    {
        var behindProxy = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:AnonymousRateLimit:Permits"] = "3",
                ["Security:AnonymousRateLimit:Window"] = "00:01:00",
                ["Security:TrustedProxies"] = "203.0.113.9",
            })
            .Build();

        await using var app = await RateLimitHttpTestHost.StartAsync($"{Database}_proxied", behindProxy);

        for (var attempt = 0; attempt < 4; attempt++)
        {
            await app.AttemptLoginAsync("203.0.113.9", forwardedFor: "198.51.100.1");
        }

        // The other half of the boundary: without this the limiter would count every request as the
        // proxy, and one client exhausting their budget would lock out the whole household.
        var otherClient = await app.AttemptLoginAsync("203.0.113.9", forwardedFor: "198.51.100.2");

        Assert.Equal(StatusCodes.Status401Unauthorized, otherClient.Response.StatusCode);
    }

    [Fact]
    public async Task An_undeclared_connection_cannot_borrow_a_declared_proxys_trust()
    {
        var behindProxy = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:AnonymousRateLimit:Permits"] = "3",
                ["Security:AnonymousRateLimit:Window"] = "00:01:00",
                ["Security:TrustedProxies"] = "203.0.113.9",
            })
            .Build();

        await using var app = await RateLimitHttpTestHost.StartAsync($"{Database}_untrusted", behindProxy);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await app.AttemptLoginAsync("198.51.100.77", forwardedFor: $"10.0.0.{attempt}");
        }

        // Declaring a proxy does not make the header believable from anywhere else. A caller
        // connecting directly is counted by its own address however it labels itself.
        var stillCounted = await app.AttemptLoginAsync("198.51.100.77", forwardedFor: "10.0.0.99");

        Assert.Equal(StatusCodes.Status429TooManyRequests, stillCounted.Response.StatusCode);
    }

    [Fact]
    public async Task An_oversized_password_is_refused_before_it_is_hashed()
    {
        await using var app = await RateLimitHttpTestHost.StartAsync($"{Database}_oversized", ThreePerMinute());

        // Well inside the request-body limit and inside the rate limit, so nothing else stops it: an
        // unbounded password would be several times the memory the throttle was sized against, spent
        // on an input that could never match a stored hash anyway.
        var refused = await app.AttemptLoginAsync("203.0.113.40", password: new string('x', 2048));

        Assert.Equal(StatusCodes.Status401Unauthorized, refused.Response.StatusCode);
    }

    [Fact]
    public async Task The_second_factor_routes_are_throttled_per_account_not_per_address()
    {
        await using var app = await RateLimitHttpTestHost.StartAsync($"{Database}_account");
        var mallory = await app.SignInNewMemberAsync("mallory");
        var alice = await app.SignInNewMemberAsync("alice");

        // A stolen session is the threat: each wrong password costs a 19 MiB Argon2id verification and
        // answers whether the guess was right, so without a bound this is a password oracle and a way
        // to run the host out of memory, from an address the per-address limit never sees.
        for (var attempt = 0; attempt < RateLimitingRegistration.AccountCredentialPermits; attempt++)
        {
            var allowed = await app.PostAsAccountAsync(
                "/api/identity/two-factor/enroll", mallory, """{"password":"guess"}""");
            Assert.Equal(StatusCodes.Status401Unauthorized, allowed.Response.StatusCode);
        }

        var refused = await app.PostAsAccountAsync(
            "/api/identity/two-factor/enroll", mallory, """{"password":"guess"}""");
        Assert.Equal(StatusCodes.Status429TooManyRequests, refused.Response.StatusCode);
        Assert.Single(refused.Response.Headers.RetryAfter.ToArray());

        // One budget across the three routes, or guessing would simply move to the next one.
        var confirm = await app.PostAsAccountAsync(
            "/api/identity/two-factor/confirm", mallory, """{"code":"000000"}""");
        Assert.Equal(StatusCodes.Status429TooManyRequests, confirm.Response.StatusCode);

        // Counted against the account, so a household member behind the same address is untouched.
        var neighbour = await app.PostAsAccountAsync(
            "/api/identity/two-factor/enroll", alice, """{"password":"guess"}""");
        Assert.Equal(StatusCodes.Status401Unauthorized, neighbour.Response.StatusCode);
    }

    [Fact]
    public async Task A_caller_without_a_session_is_refused_before_the_account_limiter_counts_it()
    {
        await using var app = await RateLimitHttpTestHost.StartAsync($"{Database}_account_anonymous");

        // The account limiter runs after authorization, so there is always an account to count: a
        // request without one is turned away by the 401 and never reaches a partition.
        for (var attempt = 0; attempt <= RateLimitingRegistration.AccountCredentialPermits; attempt++)
        {
            var refused = await app.PostAsAccountAsync(
                "/api/identity/two-factor/enroll", "not-a-session", """{"password":"guess"}""");
            Assert.Equal(StatusCodes.Status401Unauthorized, refused.Response.StatusCode);
        }
    }

    [Fact]
    public void Exactly_the_credential_routes_are_throttled()
    {
        // Enumerated rather than spot-checked, so removing the policy fails here instead of shipping.
        // The second step of a sign-in is here too: it is anonymous, and a six-digit code is worth
        // guessing in a way a 256-bit challenge is not, so the per-challenge attempt cap and this
        // per-address one bound it from both ends.
        // GET /api/identity/setup-required is deliberately absent: it performs one indexed existence
        // check and no hashing, so it is neither a credential oracle nor the expensive path, and
        // throttling it would spend a household's shared budget on page loads.
        // The authenticated second-factor routes carry their own policy, counted per account: each
        // one either verifies the password again or accepts a six-digit code, and a session is all a
        // caller needs to reach them.
        var throttled = ApiAuthorizationTests.Surface()
            .Where(route => route.RateLimitPolicy is not null)
            .OrderBy(route => route.Key, StringComparer.Ordinal)
            .Select(route => (route.Key, route.RateLimitPolicy))
            .ToArray();

        Assert.Equal(
            [
                ("POST /api/identity/login", RateLimitPolicies.AnonymousCredentials),
                ("POST /api/identity/login/two-factor", RateLimitPolicies.AnonymousCredentials),
                ("POST /api/identity/setup", RateLimitPolicies.AnonymousCredentials),
                ("POST /api/identity/two-factor/confirm", RateLimitPolicies.AccountCredentials),
                ("POST /api/identity/two-factor/disable", RateLimitPolicies.AccountCredentials),
                ("POST /api/identity/two-factor/enroll", RateLimitPolicies.AccountCredentials),
            ],
            throttled);
    }
}
