using Cinomni.Identity.Contracts;

namespace Cinomni.Identity.Persistence;

/// <summary>
/// A local user account. Identity owns credentials and is the only module that reads or
/// writes them. Role and permissions are settable
/// (an administrator promotes, demotes and disables), which is why they are not init-only: the
/// installation must be able to change its mind about someone without recreating the account.
/// </summary>
public sealed class User
{
    public Guid Id { get; init; }

    /// <summary>Login name, normalized to lower-case for case-insensitive uniqueness.</summary>
    public required string Username { get; init; }

    /// <summary>Password verifier (argon2id PHC string); never reversible, never logged.</summary>
    public required string PasswordHash { get; set; }

    public UserRole Role { get; set; }

    /// <summary>May submit requests (an administrator always may, whatever this says).</summary>
    public bool CanRequest { get; set; }

    /// <summary>Their requests are approved on submission (an administrator's always are).</summary>
    public bool RequestsAutoApproved { get; set; }

    /// <summary>
    /// How many requests this account may have open at once, or null to use the installation default.
    /// Zero means no limit — which is what an administrator effectively has.
    /// </summary>
    public int? OpenRequestLimit { get; set; }

    /// <summary>
    /// The highest age classification this account may see, or null when it is not restricted.
    /// Meaningful only together with <see cref="ContentCeilingRegion"/>: a certificate without the
    /// region it was chosen in cannot be compared.
    /// </summary>
    public string? ContentCeiling { get; set; }

    /// <summary>The region <see cref="ContentCeiling"/> was set against. Null together with the ceiling.</summary>
    public string? ContentCeilingRegion { get; set; }

    public bool IsDisabled { get; set; }

    /// <summary>
    /// The TOTP shared secret, encrypted at rest under the installation master key and bound to this
    /// account. Encrypted rather than hashed because it has to be read back to check a code, and
    /// encrypted rather than stored plainly because a secret in a database dump is the second factor
    /// handed over with the first. Null until an enrollment starts.
    /// </summary>
    public byte[]? TotpSecretCipher { get; set; }

    public byte[]? TotpSecretNonce { get; set; }

    /// <summary>Which master key encrypted it, so a rotated key is reported rather than decrypted into noise.</summary>
    public string? TotpSecretKeyId { get; set; }

    /// <summary>
    /// When the account proved it could generate a code, and therefore when the second factor started
    /// applying. Null while a secret exists but has never been confirmed — an enrollment somebody
    /// began and abandoned must not lock them out, so it is the confirmation and not the secret that
    /// turns the factor on.
    /// </summary>
    public DateTimeOffset? TotpConfirmedAt { get; set; }

    /// <summary>
    /// Codes a pending enrollment accepts before it is dropped, as many as a sign-in challenge does and
    /// for the same reason: generous for a person reading a number off a phone, nowhere near enough to
    /// search a six-digit space.
    /// </summary>
    public const int MaxEnrollmentConfirmAttempts = 5;

    /// <summary>
    /// Codes offered against the pending enrollment. Persisted rather than counted in memory, because
    /// confirming needs only a session and a restart must not hand a guesser a fresh budget; spent by a
    /// conditional update before each code is checked, so concurrent requests cannot share one attempt.
    /// </summary>
    public int TotpConfirmAttempts { get; set; }

    /// <summary>
    /// The time step of the last code this account had accepted. A code stays valid for its whole
    /// window (up to ninety seconds with the drift allowance), so one seen over a shoulder or read off a
    /// phishing page could otherwise be replayed while it lasts. Only a later step is accepted; claimed
    /// by a conditional update, so two requests racing with the same code cannot both have it.
    /// </summary>
    public long? TotpLastAcceptedStep { get; set; }

    /// <summary>Whether signing in to this account needs a second factor.</summary>
    public bool TwoFactorEnabled => TotpConfirmedAt is not null;

    /// <summary>Forgets the second factor entirely. The caller is responsible for the recovery codes.</summary>
    public void ClearTwoFactor()
    {
        TotpSecretCipher = null;
        TotpSecretNonce = null;
        TotpSecretKeyId = null;
        TotpConfirmedAt = null;
        TotpConfirmAttempts = 0;
        TotpLastAcceptedStep = null;
    }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? LastLoginAt { get; set; }

    public bool IsAdministrator => Role == UserRole.Administrator;

    /// <summary>
    /// What this account may do, as the rest of the system asks about it: an administrator is not
    /// constrained by the member permissions, so their answer is always "everything".
    /// </summary>
    public UserPermissions Permissions => IsAdministrator
        ? UserPermissions.Full
        : new UserPermissions(CanRequest, RequestsAutoApproved, OpenRequestLimit, ContentCeiling, ContentCeilingRegion);

    public UserAccount ToAccount() =>
        new(new UserId(Id), Username, Role, Permissions, IsDisabled, CreatedAt, LastLoginAt);
}
