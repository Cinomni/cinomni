using Cinomni.Kernel.Identifiers;

namespace Cinomni.Acquisition.Contracts;

/// <summary>The failures the Acquisition module answers a caller with. Stable codes the HTTP surface maps.</summary>
public static class AcquisitionErrors
{
    public const string IntentNotFound = "acquisition.intent_not_found";

    public const string NotRetryable = "acquisition.not_retryable";
}

/// <summary>Stable internal identity of an acquisition intent (UUIDv7).</summary>
public readonly record struct AcquisitionIntentId(Guid Value)
{
    public static AcquisitionIntentId New() => new(Uuid7.New());

    public override string ToString() => Value.ToString();
}

/// <summary>Stable internal identity of a single acquisition attempt (UUIDv7).</summary>
public readonly record struct AcquisitionAttemptId(Guid Value)
{
    public static AcquisitionAttemptId New() => new(Uuid7.New());

    public override string ToString() => Value.ToString();
}

/// <summary>
/// The lifecycle of the persistent acquisition goal (case #6). A download or import
/// failure returns the goal to <see cref="Searching"/> — the intent is never lost, only retried,
/// until it either lands (<see cref="Available"/>) or exhausts its attempts (<see cref="Exhausted"/>).
/// </summary>
public enum IntentState
{
    Requested = 1,
    Planned = 2,
    Searching = 3,
    CandidateSelected = 4,
    Downloading = 5,
    Importing = 6,
    Available = 7,
    Exhausted = 8,

    /// <summary>The work was removed from the catalog; the goal ends here and is never retried.</summary>
    Cancelled = 9,
}

/// <summary>The lifecycle of one immutable attempt within an intent (append-only).</summary>
public enum AttemptState
{
    Started = 1,
    Downloading = 2,
    Imported = 3,
    FailedDownload = 4,
    FailedImport = 5,
}

/// <summary>Projection of an acquisition goal's current state.</summary>
/// <param name="UnitId">
/// The catalog unit the goal is for. Trailing optional; null on a goal created before unit
/// correlation existed, and on a movie goal whose unit is simply its work id.
/// </param>
public sealed record AcquisitionIntentSummary(
    AcquisitionIntentId Id,
    Guid TargetId,
    Guid WorkId,
    IntentState State,
    int AttemptCount,
    int MaxAttempts,
    string? SelectedReleaseGuid,
    Guid? UnitId = null);

/// <summary>What a tried release was, as the indexer described it when it was chosen.</summary>
/// <param name="Seeders">Seeders the indexer reported, or null when it did not say.</param>
/// <param name="Leechers">Leechers the indexer reported, or null when it did not say.</param>
public sealed record AttemptRelease(string Title, string IndexerName, int? Seeders, int? Leechers);

/// <summary>Projection of one attempt: which release was tried and how it ended.</summary>
/// <param name="Release">What the release was; null for an attempt opened before this was recorded.</param>
public sealed record AcquisitionAttemptSummary(
    AcquisitionAttemptId Id,
    int Ordinal,
    string ReleaseGuid,
    AttemptState State,
    DateTimeOffset StartedAt,
    DateTimeOffset? ClosedAt,
    string? FailureReason,
    AttemptRelease? Release = null);

/// <summary>One immutable line of the intent's transition history (append-only, observability).</summary>
public sealed record AcquisitionHistoryEntry(
    int Seq,
    IntentState From,
    IntentState To,
    string Trigger,
    DateTimeOffset OccurredAt,
    string? Note);

/// <summary>The full, explainable view of a goal: its state, every attempt, and its history.</summary>
public sealed record AcquisitionIntentDetail(
    AcquisitionIntentSummary Intent,
    IReadOnlyList<AcquisitionAttemptSummary> Attempts,
    IReadOnlyList<AcquisitionHistoryEntry> History);
