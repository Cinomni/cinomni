using Cinomni.Identity.Contracts;
using Cinomni.Identity.Persistence;
using Cinomni.Identity.Security;
using Cinomni.Kernel.Identifiers;
using Cinomni.Kernel.Results;
using Microsoft.EntityFrameworkCore;

namespace Cinomni.Identity.Application;

/// <summary>An opaque bearer token and when it expires. The token is shown to the client once.</summary>
public sealed record IssuedToken(string Token, DateTimeOffset ExpiresAt);

/// <summary>Issues, validates and revokes session tokens.</summary>
public interface ISessionService
{
    Task<IssuedToken> IssueAsync(UserId userId, CancellationToken cancellationToken = default);

    Task<Result<AuthenticatedUser>> ValidateAsync(string token, CancellationToken cancellationToken = default);

    /// <summary>Revokes the session for the token. Returns false if it was unknown or already revoked.</summary>
    Task<bool> RevokeAsync(string token, CancellationToken cancellationToken = default);
}

public sealed class SessionService(IdentityDbContext dbContext, ITokenFactory tokenFactory) : ISessionService
{
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(30);

    private static readonly Error InvalidToken = new("identity.invalid_token", "The session token is invalid.");
    private static readonly Error ExpiredToken = new("identity.token_expired", "The session token has expired.");

    public async Task<IssuedToken> IssueAsync(UserId userId, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var (token, tokenHash) = tokenFactory.Create();

        dbContext.Sessions.Add(new Session
        {
            Id = Uuid7.New(),
            UserId = userId.Value,
            TokenHash = tokenHash,
            CreatedAt = now,
            ExpiresAt = now + SessionLifetime,
            LastActivityAt = now,
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        return new IssuedToken(token, now + SessionLifetime);
    }

    public async Task<Result<AuthenticatedUser>> ValidateAsync(string token, CancellationToken cancellationToken = default)
    {
        var tokenHash = tokenFactory.Hash(token);
        var session = await dbContext.Sessions.SingleOrDefaultAsync(s => s.TokenHash == tokenHash, cancellationToken);

        if (session is null || session.RevokedAt is not null)
        {
            return Result<AuthenticatedUser>.Failure(InvalidToken);
        }

        var now = DateTimeOffset.UtcNow;
        if (session.ExpiresAt <= now)
        {
            return Result<AuthenticatedUser>.Failure(ExpiredToken);
        }

        var user = await dbContext.Users.SingleOrDefaultAsync(u => u.Id == session.UserId, cancellationToken);
        if (user is null || user.IsDisabled)
        {
            return Result<AuthenticatedUser>.Failure(InvalidToken);
        }

        session.LastActivityAt = now;
        await dbContext.SaveChangesAsync(cancellationToken);

        return Result<AuthenticatedUser>.Success(
            new AuthenticatedUser(new UserId(user.Id), user.Username, user.Role, user.Permissions));
    }

    public async Task<bool> RevokeAsync(string token, CancellationToken cancellationToken = default)
    {
        var tokenHash = tokenFactory.Hash(token);
        var session = await dbContext.Sessions.SingleOrDefaultAsync(s => s.TokenHash == tokenHash, cancellationToken);

        if (session is null || session.RevokedAt is not null)
        {
            return false;
        }

        session.RevokedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }
}
