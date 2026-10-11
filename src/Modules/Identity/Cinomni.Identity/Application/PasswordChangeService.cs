using Cinomni.Identity.Contracts;
using Cinomni.Identity.Events;
using Cinomni.Identity.Persistence;
using Cinomni.Identity.Security;
using Cinomni.Kernel.Results;
using Cinomni.Operations.Messaging;
using Cinomni.Operations.Transactions;
using Microsoft.EntityFrameworkCore;

namespace Cinomni.Identity.Application;

/// <summary>An account changing its own password.</summary>
public interface IPasswordChangeService
{
    /// <summary>
    /// Replaces the password once the current one is presented, and ends every other session the
    /// account holds. The session making the change keeps working.
    /// </summary>
    Task<Result> ChangeAsync(
        UserId userId,
        string currentPassword,
        string newPassword,
        string currentToken,
        CancellationToken cancellationToken = default);
}

public sealed class PasswordChangeService(
    IdentityDbContext dbContext,
    IUnitOfWork unitOfWork,
    IEventBus eventBus,
    IPasswordHasher passwordHasher,
    ITokenFactory tokenFactory,
    TimeProvider clock)
    : IPasswordChangeService
{
    /// <summary>
    /// The same code a failed sign-in answers with, so the endpoint maps it to the same status. The
    /// message names the field, because here the account is known and only the password is in doubt.
    /// </summary>
    private static readonly Error WrongCurrentPassword = new(
        "identity.invalid_credentials", "The current password is not correct.");

    private static readonly Error Unchanged = new(
        "identity.password_unchanged", "The new password must be different from the current one.");

    public async Task<Result> ChangeAsync(
        UserId userId,
        string currentPassword,
        string newPassword,
        string currentToken,
        CancellationToken cancellationToken = default)
    {
        var user = await dbContext.Users.FirstOrDefaultAsync(u => u.Id == userId.Value, cancellationToken);

        // The current password first, and alone. Answering anything about the new one before it holds
        // would let a borrowed session probe the policy, and a disabled account reads like a wrong
        // password rather than confirming the one given was right.
        if (user is null
            || user.IsDisabled
            || currentPassword.Length > CredentialLimits.MaximumPasswordLength
            || !passwordHasher.Verify(currentPassword, user.PasswordHash))
        {
            return Result.Failure(WrongCurrentPassword);
        }

        if (CredentialLimits.RefusePassword(newPassword) is { } refused)
        {
            return Result.Failure(refused);
        }

        // An ordinal comparison is enough: the current password was verified a moment ago, so this is
        // the same text the stored hash stands for.
        if (string.Equals(currentPassword, newPassword, StringComparison.Ordinal))
        {
            return Result.Failure(Unchanged);
        }

        // Hashing holds 19 MiB for a moment; it happens before the transaction opens, not inside it.
        var newHash = passwordHasher.Hash(newPassword);
        var now = clock.GetUtcNow();

        user.PasswordHash = newHash;
        await dbContext.RevokeOtherSessionsAsync(userId, tokenFactory.Hash(currentToken), now, cancellationToken);

        // A sign-in that got past the old password and is waiting for its code was earned with a
        // credential that no longer exists, so it does not get to finish.
        var pending = await dbContext.LoginChallenges
            .Where(c => c.UserId == userId.Value && c.ConsumedAt == null && c.ExpiresAt > now)
            .ToListAsync(cancellationToken);
        foreach (var challenge in pending)
        {
            challenge.ConsumedAt = now;
        }

        // The new hash, the revoked sessions, the spent challenges and the event commit together.
        await unitOfWork.ExecuteAsync(async token =>
        {
            await dbContext.SaveChangesAsync(token);
            await eventBus.PublishAsync(new PasswordChanged(user.Id), token);
        }, cancellationToken);

        return Result.Success();
    }
}
