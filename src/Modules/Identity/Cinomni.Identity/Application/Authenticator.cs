using Cinomni.Identity.Contracts;
using Cinomni.Identity.Events;
using Cinomni.Identity.Persistence;
using Cinomni.Identity.Security;
using Cinomni.Kernel.Results;
using Cinomni.Operations.Messaging;
using Cinomni.Operations.Transactions;
using Microsoft.EntityFrameworkCore;

namespace Cinomni.Identity.Application;

/// <summary>
/// Validates local credentials. Failures return a single generic error and always run a hash
/// verification (even for unknown users) so neither the message nor the timing reveals
/// whether an account exists (no username enumeration).
/// </summary>
public sealed class Authenticator(
    IdentityDbContext dbContext,
    IPasswordHasher passwordHasher,
    IUnitOfWork unitOfWork,
    IEventBus eventBus)
    : IAuthenticator
{
    // A valid PHC hash used to spend comparable time when the user does not exist.
    private static readonly Lazy<string> DecoyHash = new(() => new Argon2idPasswordHasher().Hash("decoy"));

    private static readonly Error InvalidCredentials =
        new("identity.invalid_credentials", "Invalid username or password.");

    public async Task<Result<AuthenticatedUser>> AuthenticateAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        // Refused before the query and before any hashing, decoy included: an oversized credential
        // could never match a stored one, so verifying it would be spending Argon2id on an input the
        // caller chose the size of. The answer is the same generic failure as a wrong password, so
        // this cannot be used to probe the bound either.
        if (username.Length > CredentialLimits.MaximumUsernameLength
            || password.Length > CredentialLimits.MaximumPasswordLength)
        {
            return Result<AuthenticatedUser>.Failure(InvalidCredentials);
        }

        var normalized = Usernames.Normalize(username);
        var user = await dbContext.Users.SingleOrDefaultAsync(u => u.Username == normalized, cancellationToken);

        if (user is null)
        {
            passwordHasher.Verify(password, DecoyHash.Value);
            return Result<AuthenticatedUser>.Failure(InvalidCredentials);
        }

        // Verified before the disabled flag is looked at, not after: short-circuiting on it answered a
        // disabled account without spending Argon2id, and that faster reply said the name existed.
        var passwordMatches = passwordHasher.Verify(password, user.PasswordHash);
        if (user.IsDisabled || !passwordMatches)
        {
            return Result<AuthenticatedUser>.Failure(InvalidCredentials);
        }

        return Result<AuthenticatedUser>.Success(
            new AuthenticatedUser(new UserId(user.Id), user.Username, user.Role, user.Permissions));
    }

    public async Task CompleteSignInAsync(UserId userId, CancellationToken cancellationToken = default)
    {
        var user = await dbContext.Users.FirstOrDefaultAsync(u => u.Id == userId.Value, cancellationToken);
        if (user is null)
        {
            return;
        }

        // The login timestamp and its UserAuthenticated event commit together. Called once
        // the sign-in is actually complete, which for an account with a second factor is after the
        // code and not after the password.
        user.LastLoginAt = DateTimeOffset.UtcNow;
        await unitOfWork.ExecuteAsync(async token =>
        {
            await dbContext.SaveChangesAsync(token);
            await eventBus.PublishAsync(new UserAuthenticated(user.Id, user.Username), token);
        }, cancellationToken);
    }
}
