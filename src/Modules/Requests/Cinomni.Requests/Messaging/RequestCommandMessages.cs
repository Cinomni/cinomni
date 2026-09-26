using Cinomni.Kernel.Messaging;

namespace Cinomni.Requests.Messaging;

/// <summary>Stable registered names of the Requests commands.</summary>
public static class RequestCommandNames
{
    public const string FulfilMediaRequest = "requests.fulfil";
    public const string CloseFulfilledRequests = "requests.close-fulfilled";
}

/// <summary>
/// Turns an approved request into a catalogued, monitored title — enqueued when the approval event is
/// dispatched (the outbox relay runs handlers in its own transaction, so the work happens here). The
/// handler catalogues the title (→i Catalog) and pulls its metadata, then records the produced work on the
/// request. Idempotent: Catalog returns the existing work for a known external id, so a re-executed or
/// recovered command never creates a second work.
/// </summary>
public sealed record FulfilMediaRequestCommand(Guid RequestId) : ICommand;

/// <summary>
/// Closes the requests that produced a work which now has a playable asset (⇠ Catalog's
/// <c>WorkAvailable</c>): Approved → Available. Idempotent — a request already closed is skipped.
/// </summary>
public sealed record CloseFulfilledRequestsCommand(Guid WorkId) : ICommand;
