using System.Security.Claims;
using System.Text.Json.Serialization;
using Cinomni.Catalog.Contracts;
using Cinomni.Identity.Application;
using Cinomni.Identity.Contracts;
using Cinomni.Identity.Persistence;
using Cinomni.Kernel.Results;
using Cinomni.Kernel.Security;
using Cinomni.Metadata.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Cinomni.Identity.Api;

/// <summary>HTTP surface of the Identity module: first-run setup, login, session inspection and logout.</summary>
public static class IdentityEndpoints
{
    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Every body field marked [JsonRequired] below must also be non-null; see RequiredFields.
        var group = endpoints.MapGroup("/api/identity").AddEndpointFilter(RequiredFields.RefuseNullsAsync);

        // Whether first-run setup is still open (no accounts yet).
        group.MapGet("/setup-required", async (IUserProvisioning provisioning, CancellationToken cancellationToken) =>
            Results.Ok(new { setupRequired = !await provisioning.AnyUserExistsAsync(cancellationToken) }));

        // First-run: create the administrator. Only works while no account exists.
        group.MapPost("/setup", async (
            SetupRequest request,
            IUserProvisioning provisioning,
            CancellationToken cancellationToken) =>
        {
            var result = await provisioning.CreateAdminAsync(request.Username, request.Password, cancellationToken);
            return result.IsSuccess
                ? Results.Ok(new { userId = result.Value.ToString() })
                : FailureResult(result.Error);
        })
            // Anonymous by necessity — there is no account yet to authorize it — and it hands out the
            // administrator account to whoever calls first, so it is throttled like the login is.
            .RequireRateLimiting(RateLimitPolicies.AnonymousCredentials);

        // Local login: validate credentials, then issue an opaque session token.
        group.MapPost("/login", async (
            LoginRequest request,
            IAuthenticator authenticator,
            ISessionService sessions,
            ITwoFactorService twoFactor,
            CancellationToken cancellationToken) =>
        {
            var authentication = await authenticator.AuthenticateAsync(request.Username, request.Password, cancellationToken);
            if (authentication.IsFailure)
            {
                return Results.Json(
                    new { error = authentication.Error.Code, message = authentication.Error.Message },
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            // A correct password is the whole sign-in for an account without a second factor, and
            // half of it for one with. The reply says which, rather than failing and leaving a client
            // to guess that it should ask for a code.
            if (await twoFactor.IsEnabledAsync(authentication.Value.Id, cancellationToken))
            {
                var challenge = await twoFactor.IssueChallengeAsync(authentication.Value.Id, cancellationToken);
                return Results.Ok(new
                {
                    twoFactorRequired = true,
                    challenge,
                    expiresAt = DateTimeOffset.UtcNow + LoginChallenge.Lifetime,
                    // A duration, not an instant: a client counts it down from when the reply arrived,
                    // so a browser whose clock is ahead of the server's does not see the challenge
                    // lapse before it has.
                    expiresInSeconds = (int)LoginChallenge.Lifetime.TotalSeconds,
                });
            }

            await authenticator.CompleteSignInAsync(authentication.Value.Id, cancellationToken);
            var issued = await sessions.IssueAsync(authentication.Value.Id, cancellationToken);
            return Results.Ok(new { twoFactorRequired = false, token = issued.Token, expiresAt = issued.ExpiresAt });
        })
            // The one route an unauthenticated caller can spend real resources on. Every attempt costs
            // a full Argon2id verification, including for a username that does not exist — the decoy
            // that defeats account enumeration also means guessing and exhausting memory are the same
            // request. Unthrottled, it is both a password oracle and a way to run this host out of RAM.
            .RequireRateLimiting(RateLimitPolicies.AnonymousCredentials);

        // The second step of a sign-in: the challenge the first step returned, plus a code from the
        // authenticator or one of the recovery codes. Anonymous by necessity — there is still no
        // session — and throttled for the same reason the first step is.
        group.MapPost("/login/two-factor", async (
            TwoFactorLoginRequest request,
            IAuthenticator authenticator,
            ISessionService sessions,
            ITwoFactorService twoFactor,
            CancellationToken cancellationToken) =>
        {
            var redeemed = await twoFactor.RedeemChallengeAsync(request.Challenge, request.Code, cancellationToken);
            if (redeemed.IsFailure)
            {
                return Results.Json(
                    new { error = redeemed.Error.Code, message = redeemed.Error.Message },
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            await authenticator.CompleteSignInAsync(redeemed.Value, cancellationToken);
            var issued = await sessions.IssueAsync(redeemed.Value, cancellationToken);
            return Results.Ok(new { twoFactorRequired = false, token = issued.Token, expiresAt = issued.ExpiresAt });
        })
            .RequireRateLimiting(RateLimitPolicies.AnonymousCredentials);

        // Enrolling. The password comes again on purpose: turning the second factor on and off are
        // both changes to what protects the account, not preferences, so a borrowed session is not
        // enough on its own to make either.
        group.MapPost("/two-factor/enroll", async (
            TwoFactorEnrollRequest request,
            ClaimsPrincipal principal,
            ITwoFactorService twoFactor,
            CancellationToken cancellationToken) =>
        {
            if (Viewer.From(principal) is not { } viewer)
            {
                return Results.Unauthorized();
            }

            var enrollment = await twoFactor.BeginEnrollmentAsync(
                new UserId(viewer.UserId), request.Password, cancellationToken);

            return enrollment.IsSuccess
                ? Results.Ok(new { secret = enrollment.Value.Secret, enrollmentUri = enrollment.Value.EnrollmentUri })
                : FailureResult(enrollment.Error);
        })
            .RequireAuthorization()
            .RequireRateLimiting(RateLimitPolicies.AccountCredentials);

        // Confirming, which is what actually turns it on, and the only time the recovery codes exist
        // anywhere they can be read.
        group.MapPost("/two-factor/confirm", async (
            TwoFactorCodeRequest request,
            ClaimsPrincipal principal,
            ITwoFactorService twoFactor,
            CancellationToken cancellationToken) =>
        {
            if (Viewer.From(principal) is not { } viewer)
            {
                return Results.Unauthorized();
            }

            var confirmed = await twoFactor.ConfirmEnrollmentAsync(
                new UserId(viewer.UserId), request.Code, cancellationToken);

            return confirmed.IsSuccess
                ? Results.Ok(new { recoveryCodes = confirmed.Value })
                : FailureResult(confirmed.Error);
        })
            .RequireAuthorization()
            .RequireRateLimiting(RateLimitPolicies.AccountCredentials);

        // Turning it off. A POST rather than a DELETE, because it needs the password and a current
        // code in a body: minimal APIs refuse to infer a body on DELETE, and intermediaries are
        // entitled to drop one. Needs both, and takes every other session with it — the account is
        // less protected afterwards, so a session left open elsewhere matters more than it did.
        group.MapPost("/two-factor/disable", async (
            TwoFactorDisableRequest request,
            HttpContext context,
            ClaimsPrincipal principal,
            ITwoFactorService twoFactor,
            CancellationToken cancellationToken) =>
        {
            if (Viewer.From(principal) is not { } viewer)
            {
                return Results.Unauthorized();
            }

            var token = BearerTokens.Extract(context.Request.Headers.Authorization.ToString());
            var disabled = await twoFactor.DisableAsync(
                new UserId(viewer.UserId), request.Password, request.Code, token ?? string.Empty, cancellationToken);

            return disabled.IsSuccess ? Results.NoContent() : FailureResult(disabled.Error);
        })
            .RequireAuthorization()
            .RequireRateLimiting(RateLimitPolicies.AccountCredentials);

        // The currently authenticated user (from the validated bearer token).
        group.MapGet("/me", async (
            ClaimsPrincipal principal,
            ITwoFactorService twoFactor,
            IContentRatingRegion region,
            CancellationToken cancellationToken) =>
        {
            var isAdministrator = principal.FindFirstValue(AuthorizationClaims.Administrator) == "true";
            var contentCeiling = principal.FindFirstValue(AuthorizationClaims.ContentCeiling);
            var contentCeilingRegion = principal.FindFirstValue(AuthorizationClaims.ContentCeilingRegion);
            return Results.Ok(new
            {
                id = principal.FindFirstValue(ClaimTypes.NameIdentifier),
                username = principal.FindFirstValue(ClaimTypes.Name),
                isAdministrator,
                role = (isAdministrator ? UserRole.Administrator : UserRole.Member).ToString(),
                permissions = new
                {
                    canRequest = isAdministrator || principal.FindFirstValue(AuthorizationClaims.CanRequest) == "true",
                    requestsAutoApproved =
                        isAdministrator || principal.FindFirstValue(AuthorizationClaims.RequestsAutoApproved) == "true",
                    // Three states, and the account has to be able to see which one it is in: null is
                    // "the installation default applies", zero is "no cap for me", a number is the cap.
                    // Read from the claim rather than the database, and that is not the compromise it
                    // looks like next to twoFactorEnabled below: claims here are rebuilt from the
                    // account on every request, so an administrator changing this takes effect on the
                    // member's next call rather than when their session ends. Two-factor takes the
                    // other route only because it has no claim to read.
                    openRequestLimit = int.TryParse(
                        principal.FindFirstValue(AuthorizationClaims.OpenRequestLimit),
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var openRequestLimit)
                        ? openRequestLimit
                        : (int?)null,
                    contentCeiling,
                    contentCeilingRegion,
                    // A stored ceiling for a region this installation no longer uses does not filter.
                    // The account has to be able to see that, rather than believe titles are hidden.
                    contentCeilingApplies = ContentRatingScale.Applies(region.Current, contentCeiling, contentCeilingRegion),
                },
                // Read rather than claimed: the session predates any enrollment made during it, so a
                // claim would go stale the moment somebody turned the factor on.
                twoFactorEnabled = Viewer.From(principal) is { } viewer
                    && await twoFactor.IsEnabledAsync(new UserId(viewer.UserId), cancellationToken),
            });
        })
            .RequireAuthorization();

        // Account management (administrators only): list the household and invite a member.
        group.MapGet("/users", async (
            IUserProvisioning provisioning,
            IContentRatingRegion region,
            CancellationToken cancellationToken) =>
            Results.Ok((await provisioning.ListAsync(cancellationToken)).Select(user => ToDto(user, region.Current))))
            .RequireAuthorization(AuthorizationPolicies.Administrator);

        group.MapPost("/users", async (
            CreateUserRequest request,
            IUserProvisioning provisioning,
            CancellationToken cancellationToken) =>
        {
            var permissions = request.Permissions is { } p
                ? new UserPermissions(p.CanRequest, p.RequestsAutoApproved, p.OpenRequestLimit, p.ContentCeiling)
                : UserPermissions.Default;

            var result = await provisioning.CreateUserAsync(
                request.Username, request.Password, request.Role, permissions, cancellationToken);

            return result.IsSuccess
                ? Results.Created($"/api/identity/users/{result.Value}", new { userId = result.Value.ToString() })
                : FailureResult(result.Error);
        })
            .RequireAuthorization(AuthorizationPolicies.Administrator);

        // Promote, demote, disable and re-permission an account. Every one of these can lock the
        // installation out of its own operator surfaces, so the service refuses the last administrator.
        group.MapPut("/users/{id:guid}/role", async (
            Guid id,
            SetRoleRequest request,
            IUserProvisioning provisioning,
            CancellationToken cancellationToken) =>
        {
            var result = await provisioning.SetRoleAsync(new UserId(id), request.Role, cancellationToken);
            return result.IsSuccess ? Results.NoContent() : FailureResult(result.Error);
        })
            .RequireAuthorization(AuthorizationPolicies.Administrator);

        group.MapPut("/users/{id:guid}/permissions", async (
            Guid id,
            SetPermissionsRequest request,
            IUserProvisioning provisioning,
            CancellationToken cancellationToken) =>
        {
            var result = await provisioning.SetPermissionsAsync(
                new UserId(id),
                new UserPermissions(request.CanRequest, request.RequestsAutoApproved, request.OpenRequestLimit, request.ContentCeiling),
                cancellationToken);
            return result.IsSuccess ? Results.NoContent() : FailureResult(result.Error);
        })
            .RequireAuthorization(AuthorizationPolicies.Administrator);

        group.MapPut("/users/{id:guid}/disabled", async (
            Guid id,
            SetDisabledRequest request,
            IUserProvisioning provisioning,
            CancellationToken cancellationToken) =>
        {
            var result = await provisioning.SetDisabledAsync(new UserId(id), request.Disabled, cancellationToken);
            return result.IsSuccess ? Results.NoContent() : FailureResult(result.Error);
        })
            .RequireAuthorization(AuthorizationPolicies.Administrator);

        // Revoke the presented session token.
        group.MapPost("/logout", async (HttpContext context, ISessionService sessions, CancellationToken cancellationToken) =>
        {
            var token = BearerTokens.Extract(context.Request.Headers.Authorization.ToString());
            if (token is not null)
            {
                await sessions.RevokeAsync(token, cancellationToken);
            }

            return Results.NoContent();
        })
            .RequireAuthorization();

        return endpoints;
    }

    private static IResult FailureResult(Error error)
    {
        var status = error.Code switch
        {
            "identity.user_not_found" => StatusCodes.Status404NotFound,
            "identity.setup_already_completed" or "identity.username_taken" or "identity.last_administrator"
                or "identity.two_factor_already_enabled" => StatusCodes.Status409Conflict,
            // The request was valid and the installation is what cannot honour it, exactly as the
            // indexer credential store answers when the master key is missing.
            "identity.two_factor_unavailable" => StatusCodes.Status503ServiceUnavailable,
            "identity.invalid_credentials" or "identity.invalid_two_factor_code" =>
                StatusCodes.Status401Unauthorized,
            _ => StatusCodes.Status400BadRequest,
        };

        return Results.Json(new { error = error.Code, message = error.Message }, statusCode: status);
    }

    private static object ToDto(UserAccount user, string? currentRegion) => new
    {
        id = user.Id.ToString(),
        username = user.Username,
        role = user.Role.ToString(),
        isAdministrator = user.IsAdministrator,
        permissions = new
        {
            canRequest = user.Permissions.CanRequest,
            requestsAutoApproved = user.Permissions.RequestsAutoApproved,
            // Null means this account defers to the installation default rather than naming a cap.
            openRequestLimit = user.Permissions.OpenRequestLimit,
            contentCeiling = user.Permissions.ContentCeiling,
            contentCeilingRegion = user.Permissions.ContentCeilingRegion,
            contentCeilingApplies = ContentRatingScale.Applies(
                currentRegion, user.Permissions.ContentCeiling, user.Permissions.ContentCeilingRegion),
        },
        isDisabled = user.IsDisabled,
        createdAt = user.CreatedAt,
        lastLoginAt = user.LastLoginAt,
    };

    // Required on the wire: a body missing one of these was bound as null into a non-nullable string
    // and failed as a 500 far from the request. Now it is refused while being read, as a 400 in the
    // envelope (see the Host's ApiErrorHandling).
    public sealed record SetupRequest([property: JsonRequired] string Username, [property: JsonRequired] string Password);

    public sealed record LoginRequest([property: JsonRequired] string Username, [property: JsonRequired] string Password);

    /// <summary>The challenge from the first step, and a TOTP code or one of the recovery codes.</summary>
    public sealed record TwoFactorLoginRequest([property: JsonRequired] string Challenge, [property: JsonRequired] string Code);

    public sealed record TwoFactorEnrollRequest([property: JsonRequired] string Password);

    public sealed record TwoFactorCodeRequest([property: JsonRequired] string Code);

    public sealed record TwoFactorDisableRequest([property: JsonRequired] string Password, [property: JsonRequired] string Code);

    public sealed record PermissionsBody(
        bool CanRequest,
        bool RequestsAutoApproved,
        int? OpenRequestLimit = null,
        string? ContentCeiling = null);

    public sealed record CreateUserRequest(
        [property: JsonRequired] string Username,
        [property: JsonRequired] string Password,
        [property: JsonRequired] UserRole Role,
        PermissionsBody? Permissions = null);

    public sealed record SetRoleRequest([property: JsonRequired] UserRole Role);

    /// <param name="OpenRequestLimit">
    /// How many requests this account may have open at once. Omit it (or send null) to let the
    /// installation default apply; zero lifts the cap for this account alone.
    /// </param>
    public sealed record SetPermissionsRequest(
        bool CanRequest,
        bool RequestsAutoApproved,
        int? OpenRequestLimit = null,
        string? ContentCeiling = null);

    public sealed record SetDisabledRequest([property: JsonRequired] bool Disabled);
}
