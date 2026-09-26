using System.Security.Cryptography;
using System.Text;
using Cinomni.Identity.Contracts;
using Cinomni.Identity.Persistence;
using Cinomni.Identity.Security;
using Cinomni.Kernel.Results;
using Cinomni.Operations.Settings;
using Cinomni.Operations.Transactions;
using Microsoft.EntityFrameworkCore;

namespace Cinomni.Identity.Application;

/// <summary>What an enrollment needs to show once, and never again.</summary>
/// <param name="Secret">Base32, for someone typing it in rather than scanning.</param>
/// <param name="EnrollmentUri">The <c>otpauth://</c> URI behind the QR code.</param>
public sealed record TwoFactorEnrollment(string Secret, string EnrollmentUri);

/// <summary>
/// The second factor: enrolling one, confirming it, spending it at sign-in, and taking it off again.
/// <para>
/// The shape that matters is that a secret does not turn the factor on — a confirmation does. An
/// enrollment somebody started and walked away from leaves a stored secret and an account that still
/// signs in exactly as before, because the alternative is locking a person out of their own
/// installation with a QR code they never scanned.
/// </para>
/// </summary>
public interface ITwoFactorService
{
    /// <summary>Whether signing in to this account needs a second factor.</summary>
    Task<bool> IsEnabledAsync(UserId userId, CancellationToken cancellationToken = default);

    Task<Result<TwoFactorEnrollment>> BeginEnrollmentAsync(
        UserId userId, string password, CancellationToken cancellationToken = default);

    /// <summary>Confirms an enrollment and mints the recovery codes, which are returned once and never stored in the clear.</summary>
    Task<Result<IReadOnlyList<string>>> ConfirmEnrollmentAsync(
        UserId userId, string code, CancellationToken cancellationToken = default);

    /// <summary>Removes the second factor and revokes every other session the account holds.</summary>
    Task<Result> DisableAsync(
        UserId userId, string password, string code, string currentToken, CancellationToken cancellationToken = default);

    /// <summary>Opens the second step of a sign-in whose password has already been verified.</summary>
    Task<string> IssueChallengeAsync(UserId userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Answers a challenge with a TOTP code or a recovery code. Failure is deliberately one error for
    /// every cause: an expired challenge, a spent one, a wrong code and an exhausted account all read
    /// the same, because telling them apart tells an attacker which half of the credential they have.
    /// </summary>
    Task<Result<UserId>> RedeemChallengeAsync(
        string challenge, string code, CancellationToken cancellationToken = default);
}

public sealed class TwoFactorService(
    IdentityDbContext dbContext,
    IUnitOfWork unitOfWork,
    IPasswordHasher passwordHasher,
    ITokenFactory tokenFactory,
    SettingsSecretCipher cipher,
    TimeProvider clock)
    : ITwoFactorService
{
    /// <summary>Ten is the number every service that does this settles on, and it is enough.</summary>
    private const int RecoveryCodeCount = 10;

    /// <summary>160 bits, like the TOTP secret: nothing here is guessable, so nothing has to be slow.</summary>
    private const int RecoveryCodeBytes = 20;

    /// <summary>The label an authenticator application shows beside the code.</summary>
    private const string Issuer = "Cinomni";

    private static readonly Error InvalidCredentials =
        new("identity.invalid_credentials", "Invalid username or password.");

    private static readonly Error InvalidCode =
        new("identity.invalid_two_factor_code", "That code is not valid.");

    /// <summary>What turning the second factor off answers for a wrong password and a wrong code alike.</summary>
    private static readonly Error DisableRefused =
        new("identity.invalid_credentials", "The password or the code is not valid.");

    private static readonly Error AlreadyEnabled = new(
        "identity.two_factor_already_enabled",
        "Two-factor authentication is already on for this account. Turn it off before enrolling again.");

    private static readonly Error EnrollmentAbandoned = new(
        "identity.two_factor_enrollment_abandoned",
        "Too many wrong codes, so this setup was cancelled. Start again with your password.");

    private static readonly Error NotEnrolled =
        new("identity.two_factor_not_enrolled", "Two-factor authentication is not set up for this account.");

    private static readonly Error SecretUnavailable = new(
        "identity.two_factor_unavailable",
        "Two-factor authentication needs the installation secret key, which is not configured.");

    public Task<bool> IsEnabledAsync(UserId userId, CancellationToken cancellationToken = default) =>
        dbContext.Users.AsNoTracking()
            .AnyAsync(u => u.Id == userId.Value && u.TotpConfirmedAt != null, cancellationToken);

    public async Task<Result<TwoFactorEnrollment>> BeginEnrollmentAsync(
        UserId userId, string password, CancellationToken cancellationToken = default)
    {
        var user = await dbContext.Users.FirstOrDefaultAsync(u => u.Id == userId.Value, cancellationToken);
        if (user is null)
        {
            return Result<TwoFactorEnrollment>.Failure(InvalidCredentials);
        }

        // The account's own state first, the password second. The other way round, "already enabled"
        // was the answer to a right password only — a stolen session could confirm one here.
        if (user.TwoFactorEnabled)
        {
            return Result<TwoFactorEnrollment>.Failure(AlreadyEnabled);
        }

        if (!VerifyPassword(user, password))
        {
            return Result<TwoFactorEnrollment>.Failure(InvalidCredentials);
        }

        var secret = Totp.CreateSecret();
        var encrypted = cipher.Encrypt(AadFor(userId), secret);
        if (encrypted.IsFailure)
        {
            // The installation is what cannot honour this, not the request: the same distinction the
            // indexer credential store makes, and the same 503 it earns at the endpoint.
            return Result<TwoFactorEnrollment>.Failure(SecretUnavailable);
        }

        user.TotpSecretCipher = encrypted.Value.Cipher;
        user.TotpSecretNonce = encrypted.Value.Nonce;
        user.TotpSecretKeyId = encrypted.Value.KeyId;
        // Not confirmed: the account signs in exactly as it did until a code proves the secret arrived.
        user.TotpConfirmedAt = null;
        // A new secret gets a new budget; the password was just verified again to earn it. Marked
        // modified by hand: attempts are spent by set-based updates the change tracker never sees, so a
        // tracked copy can still read zero and "setting" zero would otherwise write nothing.
        user.TotpConfirmAttempts = 0;
        dbContext.Entry(user).Property(u => u.TotpConfirmAttempts).IsModified = true;
        // Steps belong to the secret they were accepted under. Marked modified for the reason above:
        // the step is claimed by set-based updates too.
        user.TotpLastAcceptedStep = null;
        dbContext.Entry(user).Property(u => u.TotpLastAcceptedStep).IsModified = true;
        await unitOfWork.ExecuteAsync(dbContext.SaveChangesAsync, cancellationToken);

        return Result<TwoFactorEnrollment>.Success(
            new TwoFactorEnrollment(secret, Totp.EnrollmentUri(Issuer, user.Username, secret)));
    }

    public async Task<Result<IReadOnlyList<string>>> ConfirmEnrollmentAsync(
        UserId userId, string code, CancellationToken cancellationToken = default)
    {
        var user = await dbContext.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId.Value, cancellationToken);
        if (user?.TotpSecretCipher is null)
        {
            return Result<IReadOnlyList<string>>.Failure(NotEnrolled);
        }

        // Confirming needs only a session, so on an account that already has the factor it must not
        // be a second way in: one guessed code would otherwise mint ten recovery codes that outlive
        // the session and void the owner's own.
        if (user.TwoFactorEnabled)
        {
            return Result<IReadOnlyList<string>>.Failure(AlreadyEnabled);
        }

        var secret = ReadSecret(user);
        if (secret is null)
        {
            return Result<IReadOnlyList<string>>.Failure(SecretUnavailable);
        }

        // The attempt is spent before the code is looked at, in one conditional statement. Counting
        // afterwards in memory would let parallel requests that all read the same count each check a
        // code while the row records one, and the cap would bound nothing.
        if (!await ReserveConfirmAttemptAsync(userId, cancellationToken))
        {
            return Result<IReadOnlyList<string>>.Failure(EnrollmentAbandoned);
        }

        if (!Totp.TryMatch(secret, code, clock.GetUtcNow(), out var step))
        {
            return await AbandonIfExhaustedAsync(userId, cancellationToken)
                ? Result<IReadOnlyList<string>>.Failure(EnrollmentAbandoned)
                : Result<IReadOnlyList<string>>.Failure(InvalidCode);
        }

        var codes = Enumerable.Range(0, RecoveryCodeCount).Select(_ => CreateRecoveryCode()).ToList();
        var now = clock.GetUtcNow();
        var confirmed = false;

        await unitOfWork.ExecuteAsync(async token =>
        {
            // Conditional, like the reservation: an enrollment abandoned or confirmed by a concurrent
            // request in the meantime is not turned on here, and no codes are minted for it.
            confirmed = await PendingEnrollment(userId).ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(u => u.TotpConfirmedAt, now)
                    .SetProperty(u => u.TotpConfirmAttempts, 0)
                    // The code that confirmed is spent: it must not also open the next sign-in.
                    .SetProperty(u => u.TotpLastAcceptedStep, step),
                token) == 1;
            if (!confirmed)
            {
                return;
            }

            // Replaces any batch a previous enrollment left behind: codes belong to the secret they were
            // minted alongside, and an old one answering for a new secret would be a way past it.
            dbContext.RecoveryCodes.RemoveRange(
                await dbContext.RecoveryCodes.Where(c => c.UserId == userId.Value).ToListAsync(token));
            dbContext.RecoveryCodes.AddRange(codes.Select(value => new RecoveryCode
            {
                Id = Guid.CreateVersion7(),
                UserId = userId.Value,
                CodeHash = HashRecoveryCode(value),
                CreatedAt = now,
            }));
            await dbContext.SaveChangesAsync(token);
        }, cancellationToken);

        return confirmed
            ? Result<IReadOnlyList<string>>.Success(codes)
            : Result<IReadOnlyList<string>>.Failure(AlreadyEnabled);
    }

    public async Task<Result> DisableAsync(
        UserId userId, string password, string code, string currentToken, CancellationToken cancellationToken = default)
    {
        var user = await dbContext.Users.FirstOrDefaultAsync(u => u.Id == userId.Value, cancellationToken);
        if (user is null)
        {
            return Result.Failure(DisableRefused);
        }

        // The account's own state before the password, for the same reason as enrolment: answered after
        // it, "not enrolled" confirmed a right password.
        if (!user.TwoFactorEnabled)
        {
            return Result.Failure(NotEnrolled);
        }

        // One answer for a wrong password and a wrong code. Two let a stolen session learn the password
        // first — "the code is wrong" confirms it — and then use it wherever else it opens something.
        // The code is only checked once the password holds, because checking it can spend a recovery code.
        if (!VerifyPassword(user, password))
        {
            return Result.Failure(DisableRefused);
        }

        // The factor being removed still has to be presented. A session alone is not enough to take
        // the second factor off the account it protects, or a stolen session would simply undo it.
        var verified = await VerifySecondFactorAsync(user, code, cancellationToken);
        if (!verified)
        {
            return Result.Failure(DisableRefused);
        }

        var now = clock.GetUtcNow();
        user.ClearTwoFactor();
        dbContext.RecoveryCodes.RemoveRange(
            await dbContext.RecoveryCodes.Where(c => c.UserId == userId.Value).ToListAsync(cancellationToken));

        // Every other session goes. Taking the second factor off is a downgrade of what protects this
        // account, so a session left open somewhere else now carries more weight than it did, not
        // less — the one making the change is the only one that keeps its footing.
        var currentHash = tokenFactory.Hash(currentToken);
        var others = await dbContext.Sessions
            .Where(s => s.UserId == userId.Value && s.RevokedAt == null && s.TokenHash != currentHash)
            .ToListAsync(cancellationToken);
        foreach (var session in others)
        {
            session.RevokedAt = now;
        }

        await unitOfWork.ExecuteAsync(dbContext.SaveChangesAsync, cancellationToken);
        return Result.Success();
    }

    public async Task<string> IssueChallengeAsync(UserId userId, CancellationToken cancellationToken = default)
    {
        var (challenge, hash) = tokenFactory.Create();
        var now = clock.GetUtcNow();

        dbContext.LoginChallenges.Add(new LoginChallenge
        {
            Id = Guid.CreateVersion7(),
            UserId = userId.Value,
            ChallengeHash = hash,
            CreatedAt = now,
            ExpiresAt = now + LoginChallenge.Lifetime,
        });

        await unitOfWork.ExecuteAsync(dbContext.SaveChangesAsync, cancellationToken);
        return challenge;
    }

    public async Task<Result<UserId>> RedeemChallengeAsync(
        string challenge, string code, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        var pending = await dbContext.LoginChallenges.AsNoTracking()
            .FirstOrDefaultAsync(c => c.ChallengeHash == tokenFactory.Hash(challenge), cancellationToken);

        // The attempt is spent before the code is looked at, in one conditional statement, for the
        // reason the enrollment cap is: parallel guesses that each read the same count would otherwise
        // all be checked while the row recorded one, and the cap would bound nothing.
        if (pending is null || !await ReserveChallengeAttemptAsync(pending.Id, now, cancellationToken))
        {
            return Result<UserId>.Failure(InvalidCode);
        }

        var user = await dbContext.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == pending.UserId, cancellationToken);
        if (user is null || user.IsDisabled || !user.TwoFactorEnabled)
        {
            // An account disabled or unenrolled between the two steps: the challenge is spent rather
            // than left open, because nothing it could be answered with should work now.
            await ConsumeChallengeAsync(pending.Id, now, cancellationToken);
            return Result<UserId>.Failure(InvalidCode);
        }

        if (!await VerifySecondFactorAsync(user, code, cancellationToken))
        {
            // Closing rather than merely counting is what makes the cap real: the next attempt finds
            // nothing to answer, so the caller has to spend a password verification to get another.
            await OpenChallenge(pending.Id)
                .Where(c => c.Attempts >= LoginChallenge.MaxAttempts)
                .ExecuteUpdateAsync(setters => setters.SetProperty(c => c.ConsumedAt, now), cancellationToken);
            return Result<UserId>.Failure(InvalidCode);
        }

        // Conditional too: of two correct answers racing on one challenge, only one gets a session.
        return await ConsumeChallengeAsync(pending.Id, now, cancellationToken)
            ? Result<UserId>.Success(new UserId(user.Id))
            : Result<UserId>.Failure(InvalidCode);
    }

    /// <summary>
    /// A TOTP code, or a recovery code spent in the act of using it. Recovery is tried second and
    /// only when the shape rules out a code, so an ordinary sign-in never touches the codes table.
    /// Spending is one conditional update, so a code presented twice at once is honoured once.
    /// </summary>
    private async Task<bool> VerifySecondFactorAsync(User user, string code, CancellationToken cancellationToken)
    {
        if (ReadSecret(user) is { } secret && Totp.TryMatch(secret, code, clock.GetUtcNow(), out var step))
        {
            // Valid is not enough: the code must also be the first use of its step. One conditional
            // statement, so the same code presented twice at once is honoured once.
            return await dbContext.Users
                .Where(u => u.Id == user.Id && (u.TotpLastAcceptedStep == null || u.TotpLastAcceptedStep < step))
                .ExecuteUpdateAsync(setters => setters.SetProperty(u => u.TotpLastAcceptedStep, step), cancellationToken) == 1;
        }

        var hash = HashRecoveryCode(code);
        var usedAt = clock.GetUtcNow();
        return await dbContext.RecoveryCodes
            .Where(c => c.UserId == user.Id && c.CodeHash == hash && c.UsedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(c => c.UsedAt, usedAt), cancellationToken) == 1;
    }

    /// <summary>The challenge's row while it can still be answered: neither spent nor out of time.</summary>
    private IQueryable<LoginChallenge> OpenChallenge(Guid id) =>
        dbContext.LoginChallenges.Where(c => c.Id == id && c.ConsumedAt == null);

    /// <summary>Takes one of the challenge's attempts, or reports that it can no longer be answered.</summary>
    private async Task<bool> ReserveChallengeAttemptAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken) =>
        await OpenChallenge(id)
            .Where(c => c.Attempts < LoginChallenge.MaxAttempts && c.ExpiresAt > now)
            .ExecuteUpdateAsync(setters => setters.SetProperty(c => c.Attempts, c => c.Attempts + 1), cancellationToken) == 1;

    private async Task<bool> ConsumeChallengeAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken) =>
        await OpenChallenge(id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(c => c.ConsumedAt, now), cancellationToken) == 1;

    /// <summary>The account's row while an enrollment is pending: a secret stored, not yet confirmed.</summary>
    private IQueryable<User> PendingEnrollment(UserId userId) =>
        dbContext.Users.Where(u => u.Id == userId.Value && u.TotpSecretCipher != null && u.TotpConfirmedAt == null);

    /// <summary>
    /// Spends one of the pending enrollment's attempts, or reports that none is left. One statement, so
    /// concurrent requests each take their own attempt instead of all reading the same count.
    /// </summary>
    private async Task<bool> ReserveConfirmAttemptAsync(UserId userId, CancellationToken cancellationToken) =>
        await PendingEnrollment(userId)
            .Where(u => u.TotpConfirmAttempts < User.MaxEnrollmentConfirmAttempts)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(u => u.TotpConfirmAttempts, u => u.TotpConfirmAttempts + 1),
                cancellationToken) == 1;

    /// <summary>
    /// Drops the pending secret once its attempts are spent. Dropping rather than merely refusing is what
    /// makes the cap real: nothing is left to confirm, so going on needs the password again.
    /// </summary>
    private async Task<bool> AbandonIfExhaustedAsync(UserId userId, CancellationToken cancellationToken) =>
        await PendingEnrollment(userId)
            .Where(u => u.TotpConfirmAttempts >= User.MaxEnrollmentConfirmAttempts)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(u => u.TotpSecretCipher, (byte[]?)null)
                    .SetProperty(u => u.TotpSecretNonce, (byte[]?)null)
                    .SetProperty(u => u.TotpSecretKeyId, (string?)null)
                    .SetProperty(u => u.TotpConfirmAttempts, 0),
                cancellationToken) == 1;

    private bool VerifyPassword(User user, string password) =>
        !user.IsDisabled
        && password.Length <= CredentialLimits.MaximumPasswordLength
        && passwordHasher.Verify(password, user.PasswordHash);

    /// <summary>
    /// The stored secret, or null when this installation cannot read it — no master key, a rotated
    /// one, or a row that fails authentication. Every one of those means the same thing to a caller:
    /// this code cannot be checked, so it is not accepted. A recovery code still can be, which is
    /// exactly the situation recovery codes exist for.
    /// </summary>
    private string? ReadSecret(User user)
    {
        if (user.TotpSecretCipher is not { } secretCipher || user.TotpSecretNonce is not { } nonce)
        {
            return null;
        }

        var read = cipher.Decrypt(AadFor(new UserId(user.Id)), secretCipher, nonce, user.TotpSecretKeyId);
        return read.IsReadable ? read.Plaintext : null;
    }

    /// <summary>Bound to the account, so a secret lifted from one row cannot be read back under another.</summary>
    private static string AadFor(UserId userId) => $"identity.totp:{userId.Value}";

    /// <summary>
    /// Base32 like the secret, because it is a string a person reads off a screen and types back in
    /// months later, and base32 is the alphabet without the characters that get confused doing that.
    /// </summary>
    private static string CreateRecoveryCode() =>
        Base32.Encode(RandomNumberGenerator.GetBytes(RecoveryCodeBytes));

    /// <summary>
    /// Normalized before hashing, so the code works however it was displayed and retyped — the
    /// separators and case a person adds are presentation, not part of the secret.
    /// </summary>
    private static string HashRecoveryCode(string code)
    {
        var normalized = new string(code.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }
}
