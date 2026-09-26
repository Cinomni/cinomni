namespace Cinomni.Identity.Persistence;

/// <summary>
/// An authenticated session bound to an opaque bearer token. Only the token's hash is
/// stored — the token itself is shown once to the client and never persisted,
/// so a database leak exposes no usable token.
/// </summary>
public sealed class Session
{
    public Guid Id { get; init; }

    public Guid UserId { get; init; }

    /// <summary>Hex-encoded SHA-256 of the opaque token.</summary>
    public required string TokenHash { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset ExpiresAt { get; init; }

    public DateTimeOffset LastActivityAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }
}
