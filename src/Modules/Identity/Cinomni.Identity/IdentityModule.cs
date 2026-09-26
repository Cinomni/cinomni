using Cinomni.Identity.Api;
using Cinomni.Identity.Application;
using Cinomni.Identity.Contracts;
using Cinomni.Identity.Events;
using Cinomni.Identity.Messaging;
using Cinomni.Identity.Persistence;
using Cinomni.Identity.Security;
using Cinomni.Kernel.Messaging;
using Cinomni.Kernel.Security;
using Cinomni.Metadata.Contracts;
using Cinomni.Operations;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Cinomni.Identity;

/// <summary>
/// Registers the Identity module: local accounts, their role and permissions, authentication and
/// session tokens. Devices land in a later increment.
/// Requires the platform kernel (<c>AddOperations</c>) to be registered first — the module
/// shares its connection and unit of work.
/// </summary>
public static class IdentityModule
{
    /// <param name="services">The composition root's service collection.</param>
    /// <param name="configureRetention">
    /// Applied to the <see cref="SessionRetentionOptions"/> defaults before they are validated and
    /// frozen; read at registration time because the cadence becomes a scheduled job.
    /// </param>
    public static IServiceCollection AddIdentityModule(
        this IServiceCollection services,
        Action<SessionRetentionOptions>? configureRetention = null)
    {
        // Shares the kernel's scoped connection so writes and outbox events are atomic.
        services.AddModuleDbContext<IdentityDbContext>();
        // Metadata replaces this when it is composed. Without it, a ceiling cannot be set and none applies.
        services.TryAddSingleton<IContentRatingRegion, UnsetContentRatingRegion>();

        services.AddIntegrationEvent<UserCreated>(IdentityEventNames.UserCreated);
        services.AddIntegrationEvent<UserAuthenticated>(IdentityEventNames.UserAuthenticated);

        var retention = new SessionRetentionOptions();
        configureRetention?.Invoke(retention);
        retention.Validate();
        services.AddSingleton(retention);

        services.AddCommand<PurgeSessionsCommand>(IdentityCommandNames.PurgeSessions);
        services.AddScoped<ICommandHandler<PurgeSessionsCommand>, PurgeSessionsCommandHandler>();
        services.AddScheduledJob<PurgeSessionsCommand>(
            "identity.retention", IdentityCommandNames.PurgeSessions, retention.Interval);

        services.AddSingleton<IPasswordHasher, Argon2idPasswordHasher>();
        services.AddSingleton<ITokenFactory, TokenFactory>();

        // One per installation: it remembers whether first-run setup has ever completed, so the
        // anonymous route that asks stops querying the database once the answer can no longer change.
        services.AddSingleton<FirstRunState>();
        services.AddScoped<IUserProvisioning, UserProvisioning>();
        services.AddScoped<IAuthenticator, Authenticator>();
        services.AddScoped<ISessionService, SessionService>();
        // TimeProvider so a test can stand at a chosen instant: a one-time password is a function of
        // the clock, and a test that cannot move the clock cannot exercise expiry at all.
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<ITwoFactorService, TwoFactorService>();

        return services;
    }

    /// <summary>
    /// Registers the opaque-bearer authentication scheme and authorization services, including the
    /// administrator policy other modules gate their operator-only endpoints on.
    /// </summary>
    public static IServiceCollection AddIdentityAuthentication(this IServiceCollection services)
    {
        services
            .AddAuthentication(OpaqueTokenAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, OpaqueTokenAuthenticationHandler>(
                OpaqueTokenAuthenticationHandler.SchemeName, configureOptions: null);

        services.AddAuthorization(options =>
        {
            options.AddPolicy(
                AuthorizationPolicies.Administrator,
                policy => policy
                    .RequireAuthenticatedUser()
                    .RequireClaim(AuthorizationClaims.Administrator, "true"));

            // An administrator may always request; a member needs the permission.
            options.AddPolicy(
                AuthorizationPolicies.CanRequest,
                policy => policy
                    .RequireAuthenticatedUser()
                    .RequireAssertion(context =>
                        context.User.HasClaim(AuthorizationClaims.Administrator, "true")
                        || context.User.HasClaim(AuthorizationClaims.CanRequest, "true")));
        });

        return services;
    }

    /// <summary>Applies pending migrations for the identity schema (idempotent).</summary>
    public static async Task MigrateIdentityAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);
    }
}
