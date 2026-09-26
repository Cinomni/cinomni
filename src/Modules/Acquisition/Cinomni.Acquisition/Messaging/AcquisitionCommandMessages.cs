using Cinomni.Acquisition.Contracts;
using Cinomni.Kernel.Messaging;

namespace Cinomni.Acquisition.Messaging;

/// <summary>Stable registered names of the Acquisition commands.</summary>
public static class AcquisitionCommandNames
{
    public const string CreateAcquisitionIntent = "acquisition.create-intent";
    public const string SelectCandidate = "acquisition.select-candidate";
    public const string MarkDownloadStarted = "acquisition.mark-download-started";
    public const string MarkDownloadCompleted = "acquisition.mark-download-completed";
    public const string MarkDownloadFailed = "acquisition.mark-download-failed";
    public const string MarkDownloadNotStarted = "acquisition.mark-download-not-started";
    public const string MarkImported = "acquisition.mark-imported";
    public const string MarkImportFailed = "acquisition.mark-import-failed";
    public const string CancelWorkGoals = "acquisition.cancel-work-goals";
    public const string MarkUnitsSatisfied = "acquisition.mark-units-satisfied";
}

/// <summary>
/// Creates (or reuses) the persistent goal for a target off the request path — enqueued by the
/// MonitoringEnabled reaction. Idempotent by <c>create-intent:{targetId}</c>.
/// </summary>
/// <param name="UnitId">
/// The catalog unit the goal is for, echoed from <c>MonitoringEnabled</c>. Trailing optional: the
/// command queue persists this payload as jsonb, so an in-flight 1.x row deserializes with it null.
/// </param>
public sealed record CreateAcquisitionIntentCommand(
    Guid TargetId,
    Guid WorkId,
    string Mode,
    Guid? UnitId = null) : ICommand;

/// <summary>Ends every goal of a work removed from the catalog (⇠ Monitoring's <c>MonitoringRemoved</c>).</summary>
public sealed record CancelWorkGoalsCommand(Guid WorkId, bool DeleteFiles = false) : ICommand;

/// <summary>
/// Records a candidate selected by Decision into a target's goal and queues its download — enqueued
/// by the ReleaseSelected reaction. Idempotent by <c>select-candidate:{evaluationId}:{targetId}</c>.
/// </summary>
/// <param name="UnitIds">
/// The catalog units the release covers, echoed from <c>ReleaseSelected</c>. Trailing optional: the
/// command queue persists this payload as jsonb, so an in-flight 1.x row deserializes with it null
/// and the goal falls back to its own work id.
/// </param>
public sealed record SelectCandidateCommand(
    Guid EvaluationId,
    Guid TargetId,
    string ReleaseGuid,
    string DownloadUrl,
    IReadOnlyList<Guid>? UnitIds = null,
    AttemptRelease? Release = null) : ICommand;

/// <summary>
/// The in-flight download began transferring — enqueued by Downloads' <c>DownloadStarted</c> reaction.
/// Confirms the attempt on the goal. Idempotent by <c>mark-download-started:{downloadTaskId}</c>.
/// </summary>
public sealed record MarkDownloadStartedCommand(Guid IntentId) : ICommand;

/// <summary>
/// The download completed — enqueued by Downloads' <c>DownloadCompleted</c> reaction. Advances the
/// goal to Importing (awaiting Import). Idempotent by <c>mark-download-completed:{downloadTaskId}</c>.
/// </summary>
public sealed record MarkDownloadCompletedCommand(Guid IntentId) : ICommand;

/// <summary>
/// The download failed — enqueued by Downloads' <c>DownloadFailed</c> reaction. Returns the goal to
/// searching or exhausts it (case #6). Idempotent by <c>mark-download-failed:{downloadTaskId}</c>.
/// </summary>
public sealed record MarkDownloadFailedCommand(Guid IntentId, string Reason) : ICommand;

/// <summary>
/// The release never became a download — enqueued by Downloads' <c>DownloadNotStarted</c> reaction.
/// Fails that attempt like a failed download. Idempotent by <c>mark-download-not-started:{attemptId}</c>.
/// </summary>
public sealed record MarkDownloadNotStartedCommand(Guid IntentId, Guid AttemptId, string Reason) : ICommand;

/// <summary>
/// The import completed — enqueued by Import's <c>ImportCompleted</c> reaction. Meets the goal
/// (Importing → Available). Idempotent by <c>mark-imported:{importJobId}</c>.
/// </summary>
public sealed record MarkImportedCommand(Guid IntentId) : ICommand;

/// <summary>
/// The import failed — enqueued by Import's <c>ImportFailed</c> reaction. Returns the goal to
/// searching or exhausts it (case #6). Idempotent by <c>mark-import-failed:{importJobId}</c>.
/// </summary>
public sealed record MarkImportFailedCommand(Guid IntentId, string Reason) : ICommand;

/// <summary>
/// Content landed for a set of catalog units — enqueued by Import's <c>ImportCompleted</c> reaction.
/// Meets every goal whose unit is in the set, which is how one season-pack attempt closes the N
/// episode goals it satisfied. Idempotent by <c>units-satisfied:{importJobId}</c>.
/// </summary>
public sealed record MarkUnitsSatisfiedCommand(IReadOnlyList<Guid> UnitIds, Guid AssetId) : ICommand;
