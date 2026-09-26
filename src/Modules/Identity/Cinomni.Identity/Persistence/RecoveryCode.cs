namespace Cinomni.Identity.Persistence;

/// <summary>
/// One single-use way back into an account whose second factor is gone. A batch is minted when
/// two-factor is confirmed, shown once, and never shown again.
/// <para>
/// They are the reason enabling two-factor authentication on a self-hosted, often
/// single-administrator installation is not a way to lose it. Two failures they cover that nothing
/// else does: an authenticator application on a phone that broke, and a master key this installation
/// can no longer read — the TOTP secret is encrypted with that key, so losing it makes every code
/// unverifiable, while a recovery code is only hashed and stays usable.
/// </para>
/// <para>
/// Hashed, not encrypted, and with SHA-256 rather than Argon2id: a code is 160 random bits, so there
/// is no guessing to slow down, and it travels the same way a session token does for the same reason.
/// A used code is kept rather than deleted, because "you already spent this one" is a different
/// answer from "that was never a code", and an account holder deserves the first.
/// </para>
/// </summary>
public sealed class RecoveryCode
{
    public Guid Id { get; init; }

    public Guid UserId { get; init; }

    /// <summary>Hex-encoded SHA-256 of the code as it was shown, normalized.</summary>
    public required string CodeHash { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>When it was spent. A code is single-use, and this is what makes that true.</summary>
    public DateTimeOffset? UsedAt { get; set; }
}
