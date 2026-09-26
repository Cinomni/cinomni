using Cinomni.Kernel.Messaging;

namespace Cinomni.Acquisition.Contracts;

/// <summary>Stable registered names of the Acquisition integration events.</summary>
public static class AcquisitionEventNames
{
    public const string AcquisitionRequested = "acquisition.requested";
    public const string CandidateSelected = "acquisition.candidate-selected";
    public const string DownloadQueued = "acquisition.download-queued";
    public const string AcquisitionSucceeded = "acquisition.succeeded";
    public const string AcquisitionFailed = "acquisition.failed";
    public const string AcquisitionRetrying = "acquisition.retrying";
    public const string AcquisitionAttemptFailed = "acquisition.attempt-failed";
    public const string AcquisitionCancelled = "acquisition.cancelled";
}

/// <summary>
/// A persistent acquisition goal was created for a monitored target (the intent is born in
/// <c>Searching</c>). Consumed by Notifications. Ids travel as raw <see cref="Guid"/> for wire
/// stability.
/// </summary>
public sealed record AcquisitionRequested(Guid IntentId, Guid WorkId, Guid TargetId) : DomainEvent
{
    public override string IdempotencyKey => $"acquisition-requested:{IntentId}";
}

/// <summary>
/// A candidate release (chosen by Decision) was accepted into the goal and an attempt opened.
/// Distinct from <see cref="DownloadQueued"/>: this records the choice, that records the hand-off.
/// </summary>
public sealed record CandidateSelected(
    Guid IntentId,
    Guid TargetId,
    Guid EvaluationId,
    string ReleaseGuid) : DomainEvent
{
    public override string IdempotencyKey => $"candidate-selected:{IntentId}:{EvaluationId}";
}

/// <summary>
/// A release was handed off to the download subsystem. Carries the reference the downloader
/// needs; <see cref="SavePath"/> is left null so Downloads resolves placement from its own config.
/// <see cref="WorkId"/>/<see cref="TargetId"/> ride along as opaque correlation the downstream
/// modules echo forward (Downloads → Import → Library/Catalog), so the eventual asset can be tied to
/// its work and monitored target without any module reaching across a boundary.
/// </summary>
/// <param name="UnitIds">
/// The catalog units this download is meant to satisfy — the work id for a movie, the episode ids
/// for a season pack. Trailing optional: this payload is persisted as jsonb, so an in-flight 1.x
/// row deserializes with it null.
/// </param>
public sealed record DownloadQueued(
    Guid AttemptId,
    Guid IntentId,
    Guid WorkId,
    Guid TargetId,
    string ReleaseGuid,
    string DownloadUrl,
    string? SavePath,
    IReadOnlyList<Guid>? UnitIds = null) : DomainEvent
{
    public override string IdempotencyKey => $"download-queued:{AttemptId}";
}

/// <summary>
/// A goal ended because its work was removed from the catalog. Consumed by Downloads, which drops every
/// torrent the goal claimed — and, when <paramref name="DeleteFiles"/> is true, the files it downloaded.
/// </summary>
public sealed record AcquisitionCancelled(Guid IntentId, Guid WorkId, bool DeleteFiles) : DomainEvent
{
    public override string IdempotencyKey => $"acquisition-cancelled:{IntentId}";
}

/// <summary>The goal was met: content was imported and is now available. Consumed by Requests/Notifications.</summary>
public sealed record AcquisitionSucceeded(Guid IntentId, Guid WorkId) : DomainEvent
{
    public override string IdempotencyKey => $"acquisition-succeeded:{IntentId}";
}

/// <summary>
/// The goal exhausted its attempts without landing content. The intent reaches a terminal
/// <c>Exhausted</c> state; a manual/scheduled retry can reopen it later.
/// </summary>
public sealed record AcquisitionFailed(Guid IntentId, Guid WorkId, string Reason, int AttemptCount) : DomainEvent
{
    // Keyed by attempt count so each distinct exhaustion is delivered once (the catalog's
    // intentId+attempt idempotency).
    public override string IdempotencyKey => $"acquisition-failed:{IntentId}:{AttemptCount}";
}

/// <summary>
/// One attempt at a goal failed — the download died or its import did — on a named release, whether or
/// not the goal has attempts left. Consumed by Decision, which stops offering that release to that goal:
/// its ranking is deterministic, so without being told it would pick the same release on the next sweep.
/// </summary>
/// <param name="Reason">What the attempt recorded, bounded; shown to an operator explaining the skip.</param>
public sealed record AcquisitionAttemptFailed(
    Guid IntentId,
    Guid TargetId,
    Guid AttemptId,
    string ReleaseGuid,
    string Reason) : DomainEvent
{
    public override string IdempotencyKey => $"acquisition-attempt-failed:{AttemptId}";
}

/// <summary>
/// An attempt failed but attempts remain — the goal survives and returns to searching
/// (case #6). Carries the next attempt ordinal for correlation.
/// </summary>
public sealed record AcquisitionRetrying(Guid IntentId, Guid TargetId, string Reason, int NextAttempt) : DomainEvent
{
    public override string IdempotencyKey => $"acquisition-retrying:{IntentId}:{NextAttempt}";
}
