using System.Security.Claims;
using System.Text.Encodings.Web;
using Cinomni.Identity.Application;
using Cinomni.Kernel.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cinomni.Identity.Api;

/// <summary>
/// Authenticates requests carrying an opaque bearer token (<c>Authorization: Bearer &lt;token&gt;</c>)
/// by validating it against active sessions. Integrates with <c>[Authorize]</c> and the
/// standard 401 challenge.
/// </summary>
public sealed class OpaqueTokenAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Bearer";

    private readonly ISessionService _sessions;

    public OpaqueTokenAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        ISessionService sessions)
        : base(options, logger, encoder)
    {
        _sessions = sessions;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Prefer the Authorization header; fall back to an `access_token` query parameter for requests a
        // native media element makes (a <video> src or HLS segment fetch cannot set an Authorization
        // header) — the same accommodation SignalR uses. The value is the same opaque session token.
        var token = BearerTokens.Extract(Request.Headers.Authorization.ToString()) ?? QueryToken();
        if (token is null)
        {
            return AuthenticateResult.NoResult();
        }

        var result = await _sessions.ValidateAsync(token);
        if (result.IsFailure)
        {
            return AuthenticateResult.Fail(result.Error.Message);
        }

        // Claims are rebuilt from the account on every request (the token is validated against the
        // database each time), so a revoked permission takes effect at once — no stale-session window.
        var user = result.Value;
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(AuthorizationClaims.Administrator, Flag(user.IsAdministrator)),
            new Claim(AuthorizationClaims.CanRequest, Flag(user.Permissions.CanRequest)),
            new Claim(AuthorizationClaims.RequestsAutoApproved, Flag(user.Permissions.RequestsAutoApproved)),
        };

        // Only when the account actually names one: an absent claim is what "use the installation
        // default" looks like on the wire, and inventing a number here would freeze today's default
        // into every live session.
        if (user.Permissions.OpenRequestLimit is { } limit)
        {
            claims = [.. claims, new Claim(
                AuthorizationClaims.OpenRequestLimit,
                limit.ToString(System.Globalization.CultureInfo.InvariantCulture))];
        }

        // Both or neither. A ceiling without the region it was chosen in must not travel: Catalog
        // would otherwise have a certificate and nothing honest to compare it with.
        if (user.Permissions.ContentCeiling is { } ceiling && user.Permissions.ContentCeilingRegion is { } ceilingRegion)
        {
            claims = [
                .. claims,
                new Claim(AuthorizationClaims.ContentCeiling, ceiling),
                new Claim(AuthorizationClaims.ContentCeilingRegion, ceilingRegion),
            ];
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    private static string Flag(bool value) => value ? "true" : "false";

    private string? QueryToken()
    {
        if (!AcceptsQueryToken(Request.Method, Request.Path))
        {
            return null;
        }

        var token = Request.Query["access_token"].ToString();
        return string.IsNullOrEmpty(token) ? null : token;
    }

    /// <summary>
    /// Whether a request may authenticate with <c>?access_token=</c>: only a read of a playback stream,
    /// the one thing a <c>&lt;video&gt;</c> element fetches without being able to send a header. Anywhere
    /// else a token in the URL is a token in proxy and access logs, and a link that acts as its owner.
    /// The live stream is not on the list: the web client reads it with the header.
    /// </summary>
    internal static bool AcceptsQueryToken(string method, PathString path)
    {
        if (!HttpMethods.IsGet(method) && !HttpMethods.IsHead(method))
        {
            return false;
        }

        // /api/playback/sessions/{id}/stream and /api/playback/sessions/{id}/hls/{file}
        var segments = (path.Value ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 5
            && segments[0].Equals("api", StringComparison.OrdinalIgnoreCase)
            && segments[1].Equals("playback", StringComparison.OrdinalIgnoreCase)
            && segments[2].Equals("sessions", StringComparison.OrdinalIgnoreCase)
            && ((segments.Length == 5 && segments[4].Equals("stream", StringComparison.OrdinalIgnoreCase))
                || (segments.Length == 6 && segments[4].Equals("hls", StringComparison.OrdinalIgnoreCase)));
    }
}

/// <summary>Parses the token out of an <c>Authorization</c> header value.</summary>
internal static class BearerTokens
{
    private const string Prefix = "Bearer ";

    public static string? Extract(string? headerValue)
    {
        if (string.IsNullOrEmpty(headerValue) || !headerValue.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = headerValue[Prefix.Length..].Trim();
        return token.Length == 0 ? null : token;
    }
}
