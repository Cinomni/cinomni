namespace Cinomni.Identity.Persistence;

/// <summary>
/// The half-finished sign-in of an account that needs a second factor: the password has been
/// verified, and nothing else has been granted.
/// <para>
/// It exists so the second step does not have to carry the password again. That matters for more
/// than tidiness: every password verification costs a full Argon2id hash, so a person fumbling a
/// six-digit code would otherwise spend that cost — and their share of the login rate limit — on each
/// attempt. Here the expensive half happens once and the cheap half is what gets retried.
/// </para>
/// <para>
/// It is a credential in its own right, so it is treated as one: only the hash of the opaque
/// challenge is stored, it expires in minutes, and it is spent on first successful use.
/// <see cref="Attempts"/> is what bounds guessing a six-digit code — a million values sounds like a
/// lot until an attacker who already has the password is allowed to keep trying.
/// </para>
/// </summary>
public sealed class LoginChallenge
{
    /// <summary>How long a person has to read a code off their phone and type it.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Wrong codes this challenge survives. Five is generous for someone reading a number and
    /// nowhere near enough to search a six-digit space, which is the whole shape of the trade.
    /// </summary>
    public const int MaxAttempts = 5;

    public Guid Id { get; init; }

    public Guid UserId { get; init; }

    /// <summary>Hex-encoded SHA-256 of the opaque challenge handed to the client.</summary>
    public required string ChallengeHash { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset ExpiresAt { get; init; }

    /// <summary>Set when the challenge is spent — successfully or by running out of attempts.</summary>
    public DateTimeOffset? ConsumedAt { get; set; }

    /// <summary>
    /// Answers offered, right or wrong. Taken by a conditional update before each code is checked, so
    /// parallel guesses cannot share one; the challenge closes when this reaches <see cref="MaxAttempts"/>.
    /// </summary>
    public int Attempts { get; set; }
}
