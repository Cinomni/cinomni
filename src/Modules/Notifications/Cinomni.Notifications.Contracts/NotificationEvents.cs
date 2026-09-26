using Cinomni.Kernel.Messaging;

namespace Cinomni.Notifications.Contracts;

/// <summary>Stable registered names of the Notifications integration events.</summary>
public static class NotificationEventNames
{
    public const string NotificationRaised = "notifications.raised";
}

/// <summary>
/// An in-app notification was persisted. The module stays terminal in the acquisition sense — nothing
/// it emits feeds back into acquisition — but a live client cannot learn that its inbox changed by
/// watching the events that <em>caused</em> the notification: those fire before the notification exists,
/// and several of them raise none at all.
/// <para>
/// The payload carries no title or body on purpose. It is a signal that the inbox changed, addressed to
/// an audience; what the reader may actually see is still answered by the inbox read model, which
/// filters per account.
/// </para>
/// </summary>
/// <param name="AdminOnly">The audience: an operator concern never reaches a household member's inbox.</param>
public sealed record NotificationRaised(
    Guid NotificationId,
    string Type,
    string Severity,
    bool AdminOnly,
    Guid? WorkId) : DomainEvent
{
    public override string IdempotencyKey => $"notification-raised:{NotificationId}";
}
