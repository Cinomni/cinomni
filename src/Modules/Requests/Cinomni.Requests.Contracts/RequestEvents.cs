using Cinomni.Kernel.Messaging;

namespace Cinomni.Requests.Contracts;

/// <summary>Stable registered names of the Requests integration events.</summary>
public static class RequestEventNames
{
    public const string MediaRequested = "requests.media-requested";
    public const string MediaRequestApproved = "requests.media-request-approved";
    public const string MediaRequestRejected = "requests.media-request-rejected";
}

/// <summary>
/// A user asked for a title. Published atomically with the request write; consumed by Notifications so an
/// administrator learns there is something to decide on.
/// </summary>
public sealed record MediaRequested(
    Guid RequestId,
    string Title,
    int? Year,
    string Provider,
    string ExternalId,
    Guid RequestedByUserId,
    string RequestedByUsername) : DomainEvent
{
    public override string IdempotencyKey => $"media-requested:{RequestId}";
}

/// <summary>
/// An administrator approved a request. This is the trigger that puts the title in the catalog: the module
/// reacts to its own event by enqueuing the fulfilment command (the write must happen outside the outbox
/// relay's transaction).
/// </summary>
public sealed record MediaRequestApproved(
    Guid RequestId,
    string Title,
    int? Year,
    string Provider,
    string ExternalId,
    Guid RequestedByUserId) : DomainEvent
{
    public override string IdempotencyKey => $"media-request-approved:{RequestId}";
}

/// <summary>An administrator rejected a request, optionally with a reason.</summary>
public sealed record MediaRequestRejected(
    Guid RequestId,
    string Title,
    Guid RequestedByUserId,
    string? Reason) : DomainEvent
{
    public override string IdempotencyKey => $"media-request-rejected:{RequestId}";
}
