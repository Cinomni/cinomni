using Cinomni.Kernel.Identifiers;
using Cinomni.Requests.Contracts;

namespace Cinomni.Requests.Persistence;

/// <summary>
/// A stored media request and its state machine. References the produced work and the requesting user by
/// id (inter-schema, no physical FK) — Requests owns neither the catalog nor the accounts. Every
/// transition is guarded and returns whether it actually moved, so a redelivered command is a no-op
/// rather than a second decision.
/// </summary>
public sealed class MediaRequestRecord
{
    private const int TitleMax = 500;
    private const int ProviderMax = 50;
    private const int ExternalIdMax = 100;
    private const int UsernameMax = 100;
    private const int NoteMax = 500;

    public Guid Id { get; init; }

    public required string Title { get; init; }

    public int? Year { get; init; }

    /// <summary>Metadata provider the request points at ("tmdb", "tvdb"), lower-cased as the sources name themselves.</summary>
    public required string Provider { get; init; }

    public required string ExternalId { get; init; }

    /// <summary>Movie or series: how fulfilment catalogues the title and which metadata it asks for.</summary>
    public MediaRequestKind Kind { get; init; } = MediaRequestKind.Movie;

    public MediaRequestStatus Status { get; private set; }

    public Guid RequestedByUserId { get; init; }

    /// <summary>Username snapshot at request time — the list stays readable even if the account is renamed.</summary>
    public required string RequestedByUsername { get; init; }

    /// <summary>The catalog work this request produced; set when the approval is fulfilled.</summary>
    public Guid? WorkId { get; private set; }

    public Guid? DecidedByUserId { get; private set; }

    /// <summary>Why the request was rejected (operator-supplied, optional).</summary>
    public string? DecisionNote { get; private set; }

    public DateTimeOffset RequestedAt { get; init; }

    public DateTimeOffset? DecidedAt { get; private set; }

    public static MediaRequestRecord Create(SubmitMediaRequest request, DateTimeOffset now) => new()
    {
        Id = Uuid7.New(),
        Title = Text.Truncate(request.Title.Trim(), TitleMax)!,
        Year = request.Year,
        Provider = Text.Truncate(request.Provider.Trim().ToLowerInvariant(), ProviderMax)!,
        ExternalId = Text.Truncate(request.ExternalId.Trim(), ExternalIdMax)!,
        Kind = request.Kind,
        Status = MediaRequestStatus.Pending,
        RequestedByUserId = request.RequestedByUserId,
        RequestedByUsername = Text.Truncate(request.RequestedByUsername.Trim(), UsernameMax)!,
        RequestedAt = now,
    };

    /// <summary>Pending → Approved. Returns false if the request was already decided.</summary>
    public bool Approve(Guid decidedByUserId, DateTimeOffset now)
    {
        if (Status != MediaRequestStatus.Pending)
        {
            return false;
        }

        Status = MediaRequestStatus.Approved;
        DecidedByUserId = decidedByUserId;
        DecidedAt = now;
        return true;
    }

    /// <summary>Pending → Rejected. Returns false if the request was already decided.</summary>
    public bool Reject(Guid decidedByUserId, string? reason, DateTimeOffset now)
    {
        if (Status != MediaRequestStatus.Pending)
        {
            return false;
        }

        Status = MediaRequestStatus.Rejected;
        DecidedByUserId = decidedByUserId;
        DecisionNote = Text.Truncate(string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(), NoteMax);
        DecidedAt = now;
        return true;
    }

    /// <summary>
    /// Records the catalog work the approval produced. Returns false if it is already attached — the
    /// fulfilment command re-runs cleanly after a crash.
    /// </summary>
    public bool AttachWork(Guid workId)
    {
        if (WorkId == workId)
        {
            return false;
        }

        WorkId = workId;
        return true;
    }

    /// <summary>Approved → Available, once the work has a playable asset. Returns false if not applicable.</summary>
    public bool MarkAvailable()
    {
        if (Status != MediaRequestStatus.Approved)
        {
            return false;
        }

        Status = MediaRequestStatus.Available;
        return true;
    }

    public MediaRequest ToContract() => new(
        new MediaRequestId(Id),
        Title,
        Year,
        Provider,
        ExternalId,
        Status,
        RequestedByUserId,
        RequestedByUsername,
        WorkId,
        DecisionNote,
        RequestedAt,
        DecidedAt,
        Kind);
}
