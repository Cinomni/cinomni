using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Cinomni.Kernel.Security;
using Cinomni.Operations.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cinomni.Host.RateLimiting;

/// <summary>
/// The throttles on the credential routes — anonymous ones counted per address, a signed-in account's
/// counted per account — and the forwarded-header handling that decides which address is counted.
/// <para>
/// It is here rather than in a module for the same reason the operator console's routes are: every
/// module references <c>Cinomni.Operations</c>, so an HTTP concern placed there would reach the whole
/// backend. A module only names the policy, from the kernel; the Host decides what it means.
/// </para>
/// <para>
/// The limiter is in-process and per-instance, which is exactly right for the single node this ships
/// as and would be wrong for anything else. If Cinomni ever runs as more than one replica, this
/// becomes a per-replica limit and the real one has to move to shared state — worth knowing before
/// somebody assumes it scales out.
/// </para>
/// </summary>
public static class RateLimitingRegistration
{
    /// <summary>Comma-separated proxy addresses or CIDR ranges whose <c>X-Forwarded-For</c> may be believed.</summary>
    internal const string TrustedProxiesPath = "Security:TrustedProxies";

    /// <summary>
    /// How many proxies stand between a client and this application. It is a count of <em>hops</em>,
    /// not of declared addresses: two edge proxies deployed for redundancy are still one hop, and
    /// deriving this from the address list would silently double what the chain is allowed to claim.
    /// </summary>
    internal const string ProxyHopCountPath = "Security:ProxyHopCount";

    /// <summary>
    /// Credential verifications allowed to run at once, across every client. Per-address limiting
    /// cannot bound this on its own: an Argon2id verification costs 19 MiB while it runs, so what has
    /// to be bounded is concurrency, not rate, and an attacker who spreads over enough addresses
    /// stays under every per-address budget while in-flight requests pile up. This is the one bound
    /// that does not depend on recognising the client, which is exactly what makes it worth having.
    /// </summary>
    private static readonly int MaxConcurrentVerifications = Environment.ProcessorCount;

    /// <summary>
    /// Requests one account may make to the second-factor routes per <see cref="AccountCredentialWindow"/>,
    /// across all three. Enough to enroll, fumble a code or two and start over; at ten a quarter hour a
    /// guessed password is worth about a thousand tries a day, and a six-digit code — already capped per
    /// enrollment — nowhere near a search.
    /// </summary>
    internal const int AccountCredentialPermits = 10;

    internal static readonly TimeSpan AccountCredentialWindow = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Registers the limiter and the two settings behind it. The refusal answers 429 with the platform
    /// error envelope and a <c>Retry-After</c>, so a client waits the right amount rather than
    /// hammering or giving up.
    /// </summary>
    public static IServiceCollection AddCinomniRateLimiting(this IServiceCollection services)
    {
        AnonymousRateLimitOptions Bind(SettingsView view) => new()
        {
            Permits = view.GetInt(AnonymousRateLimitSettingDefinitions.Permits) ?? 10,
            Window = view.GetTimeSpan(AnonymousRateLimitSettingDefinitions.Window) ?? TimeSpan.FromMinutes(1),
        };

        services.AddSettingDefinition(AnonymousRateLimitSettingDefinitions.Permits);
        services.AddSettingDefinition(AnonymousRateLimitSettingDefinitions.Window);
        services.AddLiveOptions(Bind);

        services.AddRateLimiter(limiter =>
        {
            limiter.AddPolicy(RateLimitPolicies.AnonymousCredentials, PartitionFor);
            // This limiter runs before authentication and cannot see the account, so the account policy
            // is enforced by the second limiter (UseCinomniAccountRateLimiter). It is still named here
            // because the middleware refuses an endpoint whose policy it does not know.
            limiter.AddPolicy(RateLimitPolicies.AccountCredentials, PassThrough);
            limiter.GlobalLimiter = ConcurrencyBackstop(RateLimitPolicies.AnonymousCredentials);
            limiter.OnRejected = RejectAsync;
        });

        return services;
    }

    /// <summary>
    /// The second limiter, for the routes that re-verify a credential on behalf of a signed-in account.
    /// It has to run after authentication and authorization, because what it counts is the account:
    /// a stolen session is the threat, and it can arrive from any address. Authorization turns away a
    /// caller without a session first, so every request that reaches a partition names an account.
    /// </summary>
    public static IApplicationBuilder UseCinomniAccountRateLimiter(this IApplicationBuilder app)
    {
        var options = new RateLimiterOptions
        {
            // Its own concurrency budget, the same size as the anonymous one. Together they bound the
            // Argon2id verifications in flight to twice the processor count, whoever is asking.
            GlobalLimiter = ConcurrencyBackstop(RateLimitPolicies.AccountCredentials),
            OnRejected = RejectAsync,
        };
        options.AddPolicy(RateLimitPolicies.AccountCredentials, AccountPartitionFor);
        // Already counted by the first limiter; named so this one does not refuse the endpoint.
        options.AddPolicy(RateLimitPolicies.AnonymousCredentials, PassThrough);

        return app.UseRateLimiter(options);
    }

    /// <summary>
    /// One fixed window per account. Constant rather than a console setting: it guards a signed-in
    /// account against itself being stolen, and there is no household whose legitimate use comes near it.
    /// </summary>
    private static RateLimitPartition<string> AccountPartitionFor(HttpContext context) =>
        RateLimitPartition.GetFixedWindowLimiter(
            // Authorization has already run, so the account is always there. The address fallback is
            // for a route misconfigured without authorization: strict rather than a shared free pass.
            context.User.FindFirstValue(ClaimTypes.NameIdentifier) is { Length: > 0 } account
                ? $"account|{account}"
                : $"address|{ClientKey(context)}",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = AccountCredentialPermits,
                Window = AccountCredentialWindow,
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                AutoReplenishment = true,
            });

    private static RateLimitPartition<string> PassThrough(HttpContext context) =>
        RateLimitPartition.GetNoLimiter("pass-through");

    /// <summary>
    /// One fixed window per client address. The current limits are part of the partition key on
    /// purpose: the framework builds a partition's limiter once and reuses it, so a key of the address
    /// alone would leave every client already being counted on the old numbers until their partition
    /// went idle — an operator who tightened the limit after an attack started would watch it not
    /// apply. Folding the limits in means a change takes effect on the next request; the cost is that
    /// it also resets the counters, which is the right way round for a control an operator only
    /// touches deliberately.
    /// </summary>
    private static RateLimitPartition<string> PartitionFor(HttpContext context)
    {
        var options = context.RequestServices.GetRequiredService<ILiveOptions<AnonymousRateLimitOptions>>().Current;
        var permits = Math.Max(options.Permits, 1);
        var window = options.Window > TimeSpan.Zero ? options.Window : TimeSpan.FromMinutes(1);

        return RateLimitPartition.GetFixedWindowLimiter(
            $"{ClientKey(context)}|{permits}|{window.Ticks}",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permits,
                Window = window,
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                AutoReplenishment = true,
            });
    }

    /// <summary>
    /// The client this request is counted against — a network, not an address.
    /// <para>
    /// For IPv6 that distinction is the whole control. A single commodity host is routed a /64 at
    /// minimum, so counting per address would hand one attacker 2^64 independent budgets: every
    /// request permitted, the throttle a decoration, and each fresh address also buying a live
    /// partition and its timer. Masking to the /64 makes an IPv6 client cost what an IPv4 client
    /// costs, which is the only way the two are comparable. Building the address back from the masked
    /// bytes drops any scope id with it, so a link-local peer is one client rather than one per
    /// interface.
    /// </para>
    /// <para>
    /// A connection with no remote address — which cannot happen over TCP, but would over a unix
    /// socket — shares one bucket rather than escaping the limit. It is a known consequence rather
    /// than an oversight: strict costs nothing here, and lax would be a bypass.
    /// </para>
    /// </summary>
    private static string ClientKey(HttpContext context)
    {
        if (context.Connection.RemoteIpAddress is not { } address)
        {
            return "unattributed";
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        var prefix = address.GetAddressBytes();
        Array.Clear(prefix, 8, 8);
        return $"{new IPAddress(prefix)}/64";
    }

    /// <summary>
    /// One shared concurrency budget across every route carrying <paramref name="policyName"/>, keyed
    /// off the same metadata so it follows whatever those routes turn out to be. Requests queue rather
    /// than being refused — a queued request holds a socket, not the 19 MiB the hash it is waiting for
    /// will — and every other route on the API passes through untouched.
    /// </summary>
    private static PartitionedRateLimiter<HttpContext> ConcurrencyBackstop(string policyName) =>
        PartitionedRateLimiter.Create<HttpContext, string>(context =>
            context.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName
                == policyName
                ? RateLimitPartition.GetConcurrencyLimiter(
                    policyName,
                    _ => new ConcurrencyLimiterOptions
                    {
                        PermitLimit = MaxConcurrentVerifications,
                        QueueLimit = MaxConcurrentVerifications * 4,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    })
                : RateLimitPartition.GetNoLimiter<string>("unlimited"));

    private static ValueTask RejectAsync(OnRejectedContext rejected, CancellationToken cancellationToken)
    {
        var response = rejected.HttpContext.Response;
        response.StatusCode = StatusCodes.Status429TooManyRequests;

        // The limiter knows when the window rolls over, so the client is told rather than left to
        // guess. Rounded up: a Retry-After that expires a fraction early would earn a second refusal.
        if (rejected.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            response.Headers.RetryAfter =
                ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        }

        // The same envelope every other failure on this API uses. It names no limit and no remaining
        // budget — not because that hides anything (Retry-After gives the window boundary exactly, and
        // the permit count is learned by counting) but because there is nothing a legitimate client
        // does with those numbers that Retry-After does not already tell it.
        return new ValueTask(response.WriteAsJsonAsync(
            new { error = "security.rate_limited", message = "Too many requests. Try again shortly." },
            cancellationToken));
    }

    /// <summary>
    /// Believes <c>X-Forwarded-For</c> only from the proxies an operator has named, and says at startup
    /// which address the limiter is therefore counting.
    /// <para>
    /// This matters more than it looks. Behind a reverse proxy with nothing configured, every request
    /// arrives from the proxy: the whole household collapses into one partition, so the limit meant to
    /// stop one attacker locks out everybody instead — while the attacker, sharing that same bucket,
    /// is no more restricted than anyone else. Trusting the header unconditionally is the opposite
    /// failure, since anyone can then send a different one per request and never be counted at all.
    /// Naming the proxies is the only configuration that is not one of those two mistakes.
    /// </para>
    /// </summary>
    public static IApplicationBuilder UseCinomniForwardedHeaders(
        this IApplicationBuilder app, IConfiguration configuration, ILogger logger)
    {
        var declared = (configuration[TrustedProxiesPath] ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        var proxies = new List<IPAddress>();
        // Fully qualified: HttpOverrides ships a legacy type of the same name.
        var networks = new List<System.Net.IPNetwork>();
        foreach (var entry in declared)
        {
            if (IPAddress.TryParse(entry, out var address))
            {
                proxies.Add(address);
            }
            else if (System.Net.IPNetwork.TryParse(entry, out var network))
            {
                // Containers are given their address by the runtime, so the range is often the only
                // stable thing an operator can name.
                networks.Add(network);
            }
            else
            {
                // Never dropped in silence: one unreadable entry would otherwise leave a proxy
                // untrusted, and the operator looking at a limiter that counts everyone as one client.
                logger.LogWarning(
                    "Ignoring {Path} entry '{Entry}': it is neither an IP address nor a CIDR range.",
                    TrustedProxiesPath,
                    entry);
            }
        }

        if (proxies.Count + networks.Count == 0)
        {
            logger.LogInformation(
                "Rate limiting counts the connecting address. No trusted proxy is configured ({Path}), so "
                + "behind a reverse proxy every client would share one bucket.",
                TrustedProxiesPath);
            return app;
        }

        var options = new ForwardedHeadersOptions
        {
            // Proto rides along with For, on the same declared trust and no wider: it is what tells
            // this application the request reached the proxy over TLS, and therefore the only thing
            // that lets it make the HSTS promise honestly. Nothing else in this application reads the
            // scheme, so believing it costs nothing beyond that.
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            // Hops, not declared addresses. The known lists are what make the header believable at
            // all — a forwarded address is accepted only when handed over by one of them — and this
            // bounds how far back down the chain that trust reaches.
            ForwardLimit = Math.Max(configuration.GetValue(ProxyHopCountPath, 1), 1),
        };

        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        foreach (var proxy in proxies)
        {
            options.KnownProxies.Add(proxy);
        }

        foreach (var network in networks)
        {
            options.KnownIPNetworks.Add(network);
        }

        // What this cannot check, and what DEPLOYMENT.md therefore has to state: the proxy must write
        // the header itself. A proxy that relays a client-supplied X-Forwarded-For untouched hands
        // every caller a partition key of their choosing, and from here that is indistinguishable
        // from the proxy having written it.
        logger.LogInformation(
            "Rate limiting counts the forwarded client address, trusting {ProxyCount} proxies and "
            + "{NetworkCount} ranges over {HopCount} hop(s). The proxy must set X-Forwarded-For itself.",
            proxies.Count,
            networks.Count,
            options.ForwardLimit);

        return app.UseForwardedHeaders(options);
    }
}
