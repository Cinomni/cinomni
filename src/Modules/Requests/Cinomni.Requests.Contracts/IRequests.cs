using Cinomni.Kernel.Results;

namespace Cinomni.Requests.Contracts;

/// <summary>Public commands of the Requests module: submit a request and decide on it.</summary>
public interface IMediaRequestCommands
{
    /// <summary>
    /// Records a user's request for a title. Fails if the title is already in the library or if an
    /// undecided/approved request for the same provider id already exists (one active request per title).
    /// </summary>
    Task<Result<MediaRequestId>> SubmitAsync(SubmitMediaRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Approves a pending request, emitting <c>MediaRequestApproved</c> — which is what puts the title in
    /// the catalog (⇢ fulfilment command). Idempotent: approving an already-approved request is a no-op.
    /// </summary>
    Task<Result> ApproveAsync(MediaRequestId id, Guid decidedByUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Rejects a pending request with an optional reason, emitting <c>MediaRequestRejected</c>. Idempotent.
    /// </summary>
    Task<Result> RejectAsync(
        MediaRequestId id,
        Guid decidedByUserId,
        string? reason,
        CancellationToken cancellationToken = default);
}

/// <summary>Public read model of the Requests module.</summary>
public interface IMediaRequestQuery
{
    /// <summary>
    /// Lists requests newest-first, optionally narrowed to one status and/or one requester (a non-admin
    /// only ever sees their own).
    /// </summary>
    Task<IReadOnlyList<MediaRequest>> ListAsync(
        MediaRequestStatus? status,
        Guid? requestedByUserId,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>How many requests are waiting for a decision — the administrator's badge count.</summary>
    Task<int> PendingCountAsync(CancellationToken cancellationToken = default);
}
