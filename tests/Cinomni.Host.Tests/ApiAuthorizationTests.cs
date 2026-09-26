using System.Reflection;
using System.Text.RegularExpressions;
using Cinomni.Kernel.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Cinomni.Host.Tests;

/// <summary>
/// Pins who may call what. Since Identity can mint regular accounts, "authenticated" no longer means
/// "operator": every route that changes how the installation behaves — putting a title in the library,
/// indexers, download control, imports, delivery channels, accounts — must carry the administrator policy,
/// and the routes a household member genuinely needs (browse, play, search, request, their own inbox) must
/// not. The surface comes from <c>CinomniApi.MapCinomniEndpoints</c> — the same call <c>Program</c> makes —
/// so a new endpoint fails this test until it is classified: that is the point, and it is what keeps the
/// request/approval flow from being bypassable through the API.
/// No database and no server — this reads the routing metadata the Host builds.
/// </summary>
public sealed class ApiAuthorizationTests
{
    /// <summary>Routes any signed-in account may call. Everything else on the surface is operator-only.</summary>
    private static readonly HashSet<string> UserFacing = new(StringComparer.Ordinal)
    {
        // The probes, for an orchestrator that holds no credentials. All three answer through the same
        // writer, which emits a status and a fixed check name and nothing else — no description, no
        // exception, no path, no address. /health is readiness under its historical name.
        "GET /health",
        "GET /health/live",
        "GET /health/ready",

        // What build this installation is running. Signed in is the bar: a version footer exists so
        // that a member reporting a problem can say which build they are on, and one only an
        // administrator can read does not do that job. It carries a version, a commit and a build
        // date, and nothing an operator surface would call infrastructure.
        "GET /api/system/info",

        // Identity: your own session.
        "GET /api/identity/setup-required",
        "POST /api/identity/setup",
        "POST /api/identity/login",
        // The second step of a sign-in, and anonymous for the same reason the first is: there is no
        // session yet. What it accepts is a challenge the first step issued, not a credential.
        "POST /api/identity/login/two-factor",
        // Your own second factor. Enrolling and turning it off need the account password again on top
        // of the session, because changing the protection is not a preference; confirming needs a code
        // from the secret the enrollment returned. All three are throttled per account.
        "POST /api/identity/two-factor/enroll",
        "POST /api/identity/two-factor/confirm",
        "POST /api/identity/two-factor/disable",
        "GET /api/identity/me",
        "POST /api/identity/logout",

        // Catalog: the library is for everyone; adding to it is not. Which works you actually get back is
        // decided by the collections you may browse — a filter this test cannot see, which is why the
        // integration tests are what pin the feature.
        "GET /api/catalog/works",
        // The paged twin of the list, answered through the same collection filter.
        "GET /api/catalog/works/page",
        // ...and the counts its filter controls show, over the same visible set.
        "GET /api/catalog/works/facets",
        // Collections: which shelves you may browse. Creating them and granting them is operator-only.
        "GET /api/catalog/collections",
        "GET /api/catalog/works/{id}",
        "GET /api/catalog/works/by-external/{provider}/{value}",

        // Catalog: a series' season and episode tree is the library too — it is what the detail page lists.
        "GET /api/catalog/works/{id}/seasons",
        "GET /api/catalog/works/{id}/seasons/{number}/episodes",
        "GET /api/catalog/works/{id}/episodes/{episodeId}",

        // Metadata: searching is how you pick something to request. Snapshots are operator-only: they name
        // their work, and Metadata cannot ask Catalog whether the caller may see it.
        "GET /api/metadata/search",

        // Monitoring: the two global listings are scoped to the caller's collections (MonitoringBrowse
        // narrows through Catalog's content-access authority before paging), so an authenticated but
        // unprivileged account only ever enumerates targets it may already see — this test cannot see that
        // filter, which is why the Monitoring integration tests pin it.
        "GET /api/monitoring/targets",
        "GET /api/monitoring/targets/missing",
        // Reading whether a title is watched, given its own id, is scoped the same way: a work in a
        // collection the caller was never granted answers 404, indistinguishable from a work that does
        // not exist. This test cannot see that either — it is what the Monitoring integration tests pin.
        "GET /api/monitoring/works/{workId}/target",
        // ...and so is the tree behind it. Changing any of it (policy, toggle, subtree, manual search)
        // stays operator-only.
        "GET /api/monitoring/works/{workId}/targets",
        // Upcoming airings, scoped the same way as the wanted list: a hidden work is absent.
        "GET /api/monitoring/calendar",

        // Library and playback: what you watch and where you left it.
        "GET /api/library/assets/",
        "GET /api/library/assets/{id}",
        "GET /api/subtitles/assets/{assetId}",
        "GET /api/subtitles/searches/{id}",
        "POST /api/playback/sessions",
        "POST /api/playback/sessions/{id}/progress",
        "POST /api/playback/sessions/{id}/stop",
        "GET /api/playback/sessions/{id}",
        "GET /api/playback/sessions/{id}/stream",
        "GET /api/playback/sessions/{id}/hls/{file}",
        "GET /api/playback/sessions/{id}/subtitles/{index}",
        "PUT /api/playback/sessions/{id}/subtitle",
        "GET /api/playback/progress/{assetId}",
        // Both resolve the caller from the principal, so they can only ever return your own progress:
        // the season view needs every episode's watched flag in one call, and next-up needs the ordering.
        "GET /api/playback/progress",
        "GET /api/playback/next-up/{workId}",
        // "Continue watching": your own unfinished rows, narrowed to the titles you may still see.
        "GET /api/playback/in-progress",

        // Requests: submit one and follow your own.
        "GET /api/requests/",
        "POST /api/requests/",

        // Notifications: your inbox (the read model filters out what is not yours to see).
        "GET /api/notifications/",
        "GET /api/notifications/unread-count",
        "POST /api/notifications/{id}/read",
        "POST /api/notifications/read-all",

        // The live stream. Everyone may open one — it is how the library, the inbox and their own
        // requests stop being polled — and every message on it carries the audience that decides
        // whether this reader gets it, so opening it grants nothing the REST surface would not.
        "GET /api/realtime/stream",

        // The two fallbacks the Host maps last, both public static surfaces that carry no data —
        // exactly like GET /health, and for the same reason.
        //   * the API terminator answers an unknown /api path with the platform's error envelope, so a
        //     typo returns a 404 instead of the SPA shell the web client would try to parse as JSON. It
        //     reveals only that the route does not exist, which a 404 tells an anonymous caller anyway;
        //   * the shell is index.html, served for any other non-file GET so a deep link such as
        //     /works/{id} survives a reload. It renders the sign-in view until a session exists, and
        //     every byte of data behind it still goes through the authorized /api surface above.
        "ANY /api/{**slug}",
        "GET /{*path}",
    };

    [Fact]
    public void Every_operator_route_requires_the_administrator_policy()
    {
        var offenders = Surface()
            .Where(route => !UserFacing.Contains(route.Key) && !route.RequiresAdministrator)
            .Select(route => route.Key)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"These operator routes are open to any authenticated account:{Environment.NewLine}{string.Join(Environment.NewLine, offenders)}");
    }

    [Fact]
    public void The_hardware_report_and_its_re_run_are_mapped_and_operator_only()
    {
        // It names the platform and the FFmpeg build, and a re-run starts a dozen FFmpeg processes
        // against the device: neither is something a household member reaches.
        var routes = Surface().ToDictionary(route => route.Key, StringComparer.Ordinal);

        foreach (var key in new[] { "GET /api/playback/hardware/", "POST /api/playback/hardware/probe" })
        {
            Assert.True(routes.TryGetValue(key, out var route), $"{key} is not mapped");
            Assert.True(route.RequiresAdministrator, $"{key} is open to any authenticated account");
            Assert.DoesNotContain(key, UserFacing);
        }
    }

    [Fact]
    public void No_user_facing_route_is_locked_behind_the_administrator_policy()
    {
        var overGated = Surface()
            .Where(route => UserFacing.Contains(route.Key) && route.RequiresAdministrator)
            .Select(route => route.Key)
            .ToList();

        Assert.Empty(overGated);
    }

    [Fact]
    public void Every_route_except_the_anonymous_entry_points_requires_authentication()
    {
        // Only the probes, first-run setup, login and the two static fallbacks may be reached without a
        // session. The fallbacks serve the public web shell and a "no such API route" envelope; neither
        // reads anything, so a session would gate nothing — you cannot sign in without loading the shell.
        // The probes must stay anonymous by definition: an orchestrator has no account, and what they
        // return is a status word per fixed check name, never a path, an address or a failure message.
        string[] anonymous =
        [
            "GET /health",
            "GET /health/live",
            "GET /health/ready",
            "GET /api/identity/setup-required",
            "POST /api/identity/setup",
            "POST /api/identity/login",
            "POST /api/identity/login/two-factor",
            "ANY /api/{**slug}",
            "GET /{*path}",
        ];

        var unauthenticated = Surface()
            .Where(route => !route.RequiresAuthorization && !anonymous.Contains(route.Key))
            .Select(route => route.Key)
            .ToList();

        Assert.Empty(unauthenticated);
    }

    [Fact]
    public void The_two_anonymous_fallbacks_serve_nothing_but_a_static_file_and_a_constant()
    {
        // The allowlist entries above are keyed by method and pattern, so they would keep vouching for
        // these two routes if someone replaced what they serve. This is what makes that fail: the shell
        // must stay a file with no handler at all, and the API terminator must stay a handler that takes
        // no service — the moment it accepts a DbContext or a read model, an anonymous route reads data.
        var routes = Surface();

        var shell = routes.Single(route => route.Key == "GET /{*path}");
        Assert.Null(shell.Handler);

        var terminator = routes.Single(route => route.Key == "ANY /api/{**slug}").Handler;
        Assert.NotNull(terminator);
        Assert.Empty(terminator.GetParameters());
    }

    [Fact]
    public void The_user_facing_list_describes_routes_that_actually_exist()
    {
        // Keeps this test honest: a renamed or removed route must not silently stay on the allowlist.
        var mapped = Surface().Select(route => route.Key).ToHashSet(StringComparer.Ordinal);
        var stale = UserFacing.Where(route => !mapped.Contains(route)).ToList();

        Assert.Empty(stale);
    }

    internal sealed record Route(
        string Key,
        bool RequiresAuthorization,
        bool RequiresAdministrator,
        MethodInfo? Handler,
        string? RateLimitPolicy);

    /// <summary>
    /// Composes the modules exactly as the Host does and maps their endpoints, then reads back the
    /// authorization metadata routing will apply. Nothing connects: registration is lazy, no migration runs
    /// and no server starts — the module registrations are only here so parameter binding can resolve
    /// handler services.
    /// </summary>
    internal static IReadOnlyList<Route> Surface() => Compose().Routes;

    /// <summary>The composed application and its routes — shared with the scoped-read-model guard.</summary>
    internal static (IServiceProvider Services, IReadOnlyList<Route> Routes) Compose()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddCinomniModules();
        var app = builder.Build();

        // The very method the Host calls: whatever it maps is what this test classifies.
        app.MapCinomniEndpoints();

        var routes = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(ToRoute)
            .OrderBy(route => route.Key, StringComparer.Ordinal)
            .ToList();

        return (app.Services, routes);
    }

    private static Route ToRoute(RouteEndpoint endpoint)
    {
        var method = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.FirstOrDefault() ?? "ANY";
        var authorize = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>();
        // Route constraints are noise here ("{id:guid}" reads as "{id}"): who may call it is the subject.
        var pattern = RouteConstraint.Replace(endpoint.RoutePattern.RawText ?? string.Empty, "}");

        return new Route(
            $"{method} /{pattern.TrimStart('/')}",
            RequiresAuthorization: authorize.Count > 0,
            RequiresAdministrator: authorize.Any(data => data.Policy == AuthorizationPolicies.Administrator),
            Handler: endpoint.Metadata.GetMetadata<MethodInfo>(),
            // Which throttle routing would apply, read the same way: RateLimitTests pins the set.
            RateLimitPolicy: endpoint.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName);
    }

    private static readonly Regex RouteConstraint = new(":[^}]+}", RegexOptions.Compiled);
}
