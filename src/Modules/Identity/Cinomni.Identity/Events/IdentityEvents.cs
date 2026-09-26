using Cinomni.Kernel.Messaging;

namespace Cinomni.Identity.Events;

/// <summary>Stable registered names of the Identity integration events.</summary>
public static class IdentityEventNames
{
    public const string UserCreated = "identity.user-created";
    public const string UserAuthenticated = "identity.user-authenticated";
}

/// <summary>A user account was created. Published atomically with the account write.</summary>
public sealed record UserCreated(Guid UserId, string Username) : DomainEvent
{
    // One creation per user, so the id alone deduplicates.
    public override string IdempotencyKey => $"user-created:{UserId}";
}

/// <summary>A user authenticated successfully. Published atomically with the login timestamp.</summary>
public sealed record UserAuthenticated(Guid UserId, string Username) : DomainEvent
{
    // Each login is a distinct occurrence; the event's own timestamp keeps the key unique.
    public override string IdempotencyKey => $"user-authenticated:{UserId}:{OccurredAt:O}";
}
