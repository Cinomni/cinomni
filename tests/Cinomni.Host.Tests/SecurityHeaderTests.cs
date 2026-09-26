using Cinomni.Host.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Cinomni.Host.Tests;

/// <summary>
/// The response headers a browser defends this origin with. No database and no modules: the subject
/// is the middleware, and what these pin is the policy itself — a directive quietly dropped from it
/// is a defence quietly removed, and nothing else in the suite would notice.
/// </summary>
public sealed class SecurityHeaderTests
{
    [Fact]
    public async Task Every_response_carries_the_policy_and_the_hardening_headers()
    {
        using var host = await StartAsync();

        var response = await host.GetTestClient().GetAsync("/anything");
        var csp = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));

        // The directives that carry weight, each pinned for its own reason rather than by comparing
        // the whole string, so tightening an unrelated part of the policy does not fail this test.
        Assert.Contains("script-src 'self';", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-eval", csp, StringComparison.Ordinal);
        // Inline script is the one that reaches the session token in localStorage.
        Assert.DoesNotContain("script-src 'self' 'unsafe-inline'", csp, StringComparison.Ordinal);
        // Where anything that did run could send it.
        Assert.Contains("connect-src 'self'", csp, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none';", csp, StringComparison.Ordinal);
        Assert.Contains("object-src 'none';", csp, StringComparison.Ordinal);
        Assert.Contains("base-uri 'self';", csp, StringComparison.Ordinal);

        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("DENY", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
        // Artwork comes from third-party image hosts; without this each one is told which page is
        // being viewed.
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        Assert.Equal("same-origin", Assert.Single(response.Headers.GetValues("Cross-Origin-Opener-Policy")));
    }

    [Fact]
    public async Task The_policy_allows_what_the_player_and_the_artwork_actually_need()
    {
        using var host = await StartAsync();

        var csp = Assert.Single(
            (await host.GetTestClient().GetAsync("/anything")).Headers.GetValues("Content-Security-Policy"));

        // hls.js builds its demuxer worker from a blob and attaches the stream through a blob URL;
        // without these two, transcoded playback fails while direct play keeps working, which is a
        // confusing way to find out.
        Assert.Contains("worker-src 'self' blob:;", csp, StringComparison.Ordinal);
        Assert.Contains("media-src 'self' blob:;", csp, StringComparison.Ordinal);
        // A poster is a URL at the metadata provider, and which provider is configuration.
        Assert.Contains("img-src 'self' https: data:;", csp, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hsts_is_never_promised_on_a_deployment_that_has_not_claimed_tls()
    {
        using var host = await StartAsync();

        var overHttps = await host.GetTestClient().GetAsync("https://localhost/anything");

        // The default, and it stays the default even for a request that looks secure. Inferring the
        // promise from the scheme alone would read a six-month commitment out of a request header:
        // behind a proxy that relays client headers rather than writing its own, one X-Forwarded-Proto
        // would tell every browser to refuse plain HTTP to a plain-HTTP installation, locking the
        // household out of its own server until each of them cleared its HSTS state.
        Assert.False(overHttps.Headers.Contains("Strict-Transport-Security"));
    }

    [Fact]
    public async Task Hsts_is_promised_only_where_it_can_be_kept()
    {
        using var host = await StartAsync(hsts: true);

        var overHttp = await host.GetTestClient().GetAsync("http://localhost/anything");
        var overHttps = await host.GetTestClient().GetAsync("https://localhost/anything");

        // Declared, and then only on the requests that actually arrived over TLS — a browser must
        // ignore the header over plain HTTP anyway, so sending it there is noise.
        Assert.False(overHttp.Headers.Contains("Strict-Transport-Security"));
        var hsts = Assert.Single(overHttps.Headers.GetValues("Strict-Transport-Security"));
        Assert.Equal("max-age=15552000", hsts);

        // Neither of the two that commit a whole domain rather than this application, one of them
        // close to irreversibly. An operator who wants them sets them where TLS terminates.
        Assert.DoesNotContain("includeSubDomains", hsts, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("preload", hsts, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<IHost> StartAsync(bool hsts = false) =>
        await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(
                    new Dictionary<string, string?> { ["Security:Hsts"] = hsts ? "true" : "false" }))
                .Configure(app =>
                {
                    app.UseCinomniSecurityHeaders();
                    app.Run(context => context.Response.WriteAsync("ok"));
                }))
            .StartAsync();
}
