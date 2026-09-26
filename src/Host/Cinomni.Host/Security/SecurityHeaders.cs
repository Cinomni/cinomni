using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace Cinomni.Host.Security;

/// <summary>
/// The response headers a browser needs in order to defend this application, on every response the
/// Host serves — the web client and the API alike.
/// <para>
/// The one that earns its place is the content security policy, and the reason is specific to this
/// client: the session token lives in <c>localStorage</c>, so any script that runs on this origin can
/// read it and use it from anywhere. <c>script-src 'self'</c> with no <c>unsafe-inline</c> and no
/// <c>unsafe-eval</c> is what stands between an injected string and that token, and
/// <c>connect-src 'self'</c> is what stops anything that did run from sending it somewhere.
/// </para>
/// <para>
/// They are set here rather than left to a reverse proxy because an installation reached over a
/// private network has no proxy, and the token is just as readable there. A proxy that sets its own
/// copy of any of these overrides what is written here, which is the right way round.
/// </para>
/// </summary>
public static class SecurityHeaders
{
    /// <summary>
    /// The policy, derived from what the built client actually does — verified against the production
    /// bundle rather than assumed:
    /// <list type="bullet">
    /// <item><c>script-src 'self'</c>: the built <c>index.html</c> carries no inline script, and
    /// neither the application bundle nor the hls.js chunk contains <c>eval</c>, so neither
    /// <c>unsafe-inline</c> nor <c>unsafe-eval</c> is needed.</item>
    /// <item><c>worker-src</c> and <c>child-src</c> allow <c>blob:</c> because hls.js runs its
    /// demuxer in a worker it builds from a blob, and <c>media-src</c> does because Media Source
    /// Extensions attaches the stream to the element as a blob URL. Without them, every transcoded
    /// playback fails and direct play does not.</item>
    /// <item><c>img-src</c> allows any HTTPS origin because artwork is served by the metadata
    /// provider, not by this installation — the poster of a work is a TMDB or TheTVDB URL, and the
    /// provider's image host is configuration, so no fixed allowlist could stay correct. An image
    /// cannot execute, and <c>connect-src 'self'</c> still bounds where anything could send data.</item>
    /// <item><c>style-src</c> allows <c>unsafe-inline</c>. It is the one concession, and it is made
    /// knowingly: a component setting an inline style is ordinary React, the failure would be silent
    /// and cosmetic rather than loud, and permitting inline CSS does not let anything execute.</item>
    /// <item><c>frame-ancestors 'none'</c>, <c>object-src 'none'</c>, <c>base-uri 'self'</c>: nothing
    /// here is meant to be framed, no plugin content is served, and a rewritten base tag is a way to
    /// redirect every relative URL on the page.</item>
    /// </list>
    /// </summary>
    private const string ContentSecurityPolicy =
        "default-src 'self'; "
        + "base-uri 'self'; "
        + "object-src 'none'; "
        + "frame-ancestors 'none'; "
        + "form-action 'self'; "
        + "script-src 'self'; "
        + "worker-src 'self' blob:; "
        + "child-src 'self' blob:; "
        + "style-src 'self' 'unsafe-inline'; "
        + "img-src 'self' https: data:; "
        + "media-src 'self' blob:; "
        + "font-src 'self' data:; "
        + "connect-src 'self'";

    /// <summary>
    /// Six months, and deliberately not <c>includeSubDomains</c> or <c>preload</c>. Both of those
    /// commit a whole domain rather than this application, and one of them is close to irreversible;
    /// an operator hosting Cinomni on a subdomain of something else must not have that decided for
    /// them by a media server. A deployment that wants either sets it at the proxy, where the TLS it
    /// describes actually terminates.
    /// </summary>
    private const string StrictTransportSecurity = "max-age=15552000";

    /// <summary>
    /// Whether this deployment terminates TLS in front of the application. Off by default, and an
    /// explicit statement rather than something inferred.
    /// <para>
    /// Inferring it from <c>X-Forwarded-Proto</c> alone would be reading a durable commitment out of
    /// a request header. On a plain-HTTP deployment behind a declared proxy that relays client
    /// headers rather than writing its own, one request carrying that header would make this
    /// installation tell every browser to refuse plain HTTP to it for six months — a household locked
    /// out of its own server, undone only by clearing HSTS state in each browser. That is a bad
    /// enough outcome to be worth a switch: the deployment knows whether it has TLS, so it says so.
    /// </para>
    /// </summary>
    internal const string HstsEnabledPath = "Security:Hsts";

    public static IApplicationBuilder UseCinomniSecurityHeaders(this IApplicationBuilder app)
    {
        var hsts = app.ApplicationServices.GetService(typeof(IConfiguration)) is IConfiguration configuration
            && configuration.GetValue(HstsEnabledPath, false);

        return app.Use(async (context, next) =>
        {
            // On starting rather than now: a handler further along may set its own, and the last
            // writer before the body should win rather than the first.
            context.Response.OnStarting(static state =>
            {
                var (current, hstsEnabled) = ((HttpContext, bool))state;
                var headers = current.Response.Headers;
                headers.ContentSecurityPolicy = ContentSecurityPolicy;
                headers.XContentTypeOptions = "nosniff";
                // frame-ancestors already says this to anything modern; this is for what does not
                // implement it yet, and costs one header.
                headers.XFrameOptions = "DENY";
                // Artwork is fetched from third-party image hosts, so every poster on a page would
                // otherwise hand that host the URL of the page being viewed.
                headers["Referrer-Policy"] = "no-referrer";
                headers["Cross-Origin-Opener-Policy"] = "same-origin";

                // Both conditions, and neither is redundant. The switch is the operator stating that
                // TLS terminates in front, which is the fact the header asserts and the one thing no
                // request can establish. The scheme check is because a browser must ignore the header
                // over plain HTTP anyway, so sending it there would be noise.
                if (hstsEnabled && current.Request.IsHttps)
                {
                    headers.StrictTransportSecurity = StrictTransportSecurity;
                }

                return Task.CompletedTask;
            }, (context, hsts));

            await next();
        });
    }
}
