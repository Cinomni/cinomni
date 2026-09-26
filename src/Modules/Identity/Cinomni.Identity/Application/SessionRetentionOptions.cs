namespace Cinomni.Identity.Application;

/// <summary>
/// How long a dead session row is kept after it stops being usable.
/// <para>
/// This one is a security measure before it is a size measure: the row holds the SHA-256 of a bearer
/// token that can no longer authenticate anything, and residue that serves no purpose is residue an
/// attacker with read access to the database gets for free. Expired and revoked sessions are removed
/// once the grace period has passed; a live session is never touched.
/// </para>
/// </summary>
public sealed class SessionRetentionOptions
{
    /// <summary>
    /// How long after expiry or revocation the row is kept. A grace period rather than an immediate
    /// delete so an operator investigating "who was signed in when this happened" still can.
    /// <para>
    /// <b>This is also the deployment's ceiling on authentication forensics.</b> There is no separate
    /// auth event log: <c>identity.sessions</c> is the only record that a session ever existed, so once
    /// the grace period passes there is no evidence of it left. Thirty days is the deliberate default —
    /// long enough for an incident to surface, short enough that a dead token hash is not kept for ever.
    /// Raise it if the installation needs a longer investigative window.
    /// </para>
    /// </summary>
    public TimeSpan SessionGrace { get; set; } = TimeSpan.FromDays(30);

    /// <summary>How often <c>identity.retention</c> runs.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromDays(1);

    /// <exception cref="InvalidOperationException">The configured values are unusable.</exception>
    public void Validate()
    {
        if (SessionGrace <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"Retention:Identity:{nameof(SessionGrace)} must be a positive duration "
                + $"(configured: {SessionGrace}).");
        }

        if (Interval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"Retention:Identity:{nameof(Interval)} must be a positive duration (configured: {Interval}).");
        }
    }
}
