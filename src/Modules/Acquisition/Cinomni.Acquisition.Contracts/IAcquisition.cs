using Cinomni.Kernel.Results;

namespace Cinomni.Acquisition.Contracts;

/// <summary>
/// Write surface of the Acquisition module. Both operations are idempotent: creating an intent is
/// keyed by target (one persistent goal per monitored target), selecting a candidate is keyed by
/// the originating evaluation.
/// </summary>
public interface IAcquisitionCommands
{
    /// <summary>
    /// An administrator asked to try this goal again now. An exhausted goal is reopened with a fresh
    /// attempt budget; a searching one is left as it is. Either way the caller then asks Monitoring for a
    /// search of <see cref="AcquisitionIntentSummary.TargetId"/>, which is what actually finds a release.
    /// </summary>
    /// <returns>
    /// The goal as it now stands; <see cref="AcquisitionErrors.IntentNotFound"/> or
    /// <see cref="AcquisitionErrors.NotRetryable"/> otherwise.
    /// </returns>
    Task<Result<AcquisitionIntentSummary>> RetryAsync(Guid intentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels every goal of a work removed from the catalog. Idempotent: a goal already cancelled is left
    /// as it is, so a redelivery changes nothing. Each goal it cancels is announced with
    /// <c>AcquisitionCancelled</c>, which is what makes Downloads drop the goal's torrents.
    /// </summary>
    /// <param name="deleteFiles">Carried onward: whether the downloaded files should be deleted too.</param>
    Task CancelWorkGoalsAsync(Guid workId, bool deleteFiles, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates the persistent goal for a monitored target and drives it to <c>Searching</c>
    /// (Requested→Planned→Searching), emitting <c>AcquisitionRequested</c>. No-op if the target
    /// already has an intent; returns the existing id.
    /// </summary>
    /// <param name="unitId">
    /// The catalog unit the goal is for (the work id for a movie, the season/episode id for a series
    /// leaf). Optional and trailing so existing callers are unaffected; supplying it is what lets an
    /// import close the sibling goals one season pack satisfied.
    /// </param>
    Task<AcquisitionIntentId> CreateIntentAsync(
        Guid targetId,
        Guid workId,
        string mode,
        Guid? unitId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a candidate selected by Decision into the target's goal and hands its download off
    /// (Searching→CandidateSelected→Downloading), emitting <c>CandidateSelected</c> and
    /// <c>DownloadQueued</c>. No-op if the goal is not searchable or the evaluation was already
    /// selected.
    /// </summary>
    /// <param name="unitIds">
    /// The catalog units the selected release covers. Optional and trailing so existing callers are
    /// unaffected; when omitted the goal's own work id is used, which is the movie unit.
    /// </param>
    /// <param name="release">What the release is, kept on the attempt so it can be shown; optional.</param>
    Task SelectCandidateAsync(
        Guid evaluationId,
        Guid targetId,
        string releaseGuid,
        string downloadUrl,
        IReadOnlyList<Guid>? unitIds = null,
        AttemptRelease? release = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Confirms the in-flight attempt began transferring (⇠ Downloads' <c>DownloadStarted</c>).
    /// No-op unless the goal is still Downloading (idempotent, tolerant of out-of-order delivery).
    /// </summary>
    Task MarkDownloadStartedAsync(Guid intentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Advances the goal to Importing on a completed download (⇠ Downloads' <c>DownloadCompleted</c>).
    /// No-op unless the goal is still Downloading.
    /// </summary>
    Task MarkDownloadCompletedAsync(Guid intentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the goal to searching (or exhausts it) on a failed download (⇠ Downloads'
    /// <c>DownloadFailed</c>), emitting <c>AcquisitionRetrying</c>/<c>AcquisitionFailed</c>. No-op
    /// unless the goal is still Downloading.
    /// </summary>
    Task MarkDownloadFailedAsync(Guid intentId, string reason, CancellationToken cancellationToken = default);

    /// <summary>
    /// The same outcome for a release that never became a download at all (⇠ Downloads'
    /// <c>DownloadNotStarted</c>). Applies only while <paramref name="attemptId"/> is still the goal's
    /// current attempt, so a late report about an earlier attempt cannot fail a later one.
    /// </summary>
    Task MarkDownloadNotStartedAsync(
        Guid intentId, Guid attemptId, string reason, CancellationToken cancellationToken = default);

    /// <summary>
    /// Meets the goal on a successful import (⇠ Import's <c>ImportCompleted</c>): Importing → Available,
    /// emitting <c>AcquisitionSucceeded</c>. No-op unless the goal is still Importing (idempotent,
    /// tolerant of out-of-order delivery).
    /// </summary>
    Task MarkImportedAsync(Guid intentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the goal to searching (or exhausts it) on a failed import (⇠ Import's
    /// <c>ImportFailed</c>), emitting <c>AcquisitionRetrying</c>/<c>AcquisitionFailed</c>. No-op
    /// unless the goal is still Importing.
    /// </summary>
    Task MarkImportFailedAsync(Guid intentId, string reason, CancellationToken cancellationToken = default);

    /// <summary>
    /// Meets every goal whose unit is in <paramref name="unitIds"/> (⇠ Import's <c>ImportCompleted</c>),
    /// emitting one <c>AcquisitionSucceeded</c> per goal met. This is how one season-pack attempt
    /// closes the N episode goals it satisfied — goals that never opened an attempt of their own and
    /// otherwise had no legal way to reach <c>Available</c>. Goals still downloading are left alone,
    /// and re-running is a no-op (the import feedback is at-least-once).
    /// </summary>
    Task MarkUnitsSatisfiedAsync(
        IReadOnlyList<Guid> unitIds,
        Guid assetId,
        CancellationToken cancellationToken = default);
}

/// <summary>Read model of acquisition goals and their attempt/history trail.</summary>
public interface IAcquisitionQuery
{
    Task<IReadOnlyList<AcquisitionIntentSummary>> ListAsync(CancellationToken cancellationToken = default);

    Task<AcquisitionIntentSummary?> GetByTargetAsync(Guid targetId, CancellationToken cancellationToken = default);

    Task<AcquisitionIntentDetail?> GetAsync(Guid intentId, CancellationToken cancellationToken = default);
}
