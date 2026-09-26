using Cinomni.Kernel.Messaging;

namespace Cinomni.Import.Messaging;

/// <summary>Stable registered names of the Import commands.</summary>
public static class ImportCommandNames
{
    public const string ProcessCompletedDownload = "import.process-completed-download";

    public const string RetryImportJob = "import.retry-job";

    public const string RepairLibraryPaths = "import.repair-library-paths";

    public const string DeleteWorkFiles = "import.delete-work-files";
}

/// <summary>
/// Repairs library names an earlier build wrote before the sanitiser stopped depending on the
/// platform it ran on — enqueued by an administrator, never automatically. Queued rather than run in
/// the request because it walks the library and moves files: an HTTP timeout must not abandon it
/// half-way, and the queue is what makes it survive a restart.
/// <para>
/// Idempotent by <c>repair-library-paths:{runId}</c>, with the run id minted per request. Keying on
/// the command alone would make a deliberate second pass — after an operator has cleared whatever
/// blocked the first — a duplicate the queue silently drops.
/// </para>
/// </summary>
public sealed record RepairLibraryPathsCommand(Guid RunId) : ICommand;

/// <summary>
/// Lands a completed download into the library — enqueued by Downloads' <c>DownloadCompleted</c>
/// reaction. The file operations and ffprobe run out-of-process, so the work must be recoverable and
/// retryable; idempotent by <c>process-completed-download:{downloadTaskId}</c>.
/// </summary>
/// <param name="UnitIds">
/// The catalog units the download was meant to satisfy, echoed from <c>DownloadCompleted</c>.
/// Trailing optional: the command queue persists this payload as jsonb, so an in-flight 1.x row
/// deserializes with it null and the job falls back to its work id.
/// </param>
public sealed record ProcessCompletedDownloadCommand(
    Guid DownloadTaskId,
    Guid IntentId,
    Guid AttemptId,
    Guid WorkId,
    Guid TargetId,
    string ContentPath,
    IReadOnlyList<Guid>? UnitIds = null) : ICommand;

/// <summary>
/// Re-drives an import job that a crash left in <c>Pending</c> — enqueued by startup recovery, never
/// by an event. It exists because <c>process-completed-download:{downloadTaskId}</c> is spent the
/// moment the first hand-off is queued and the command queue drops a repeat of a spent key: without
/// a second, separately keyed way in, a job that returned to <c>Pending</c> from a partial batch is
/// unreachable for the life of the installation, and the files it did not land never arrive.
/// <para>
/// Idempotent by <c>retry-import-job:{importJobId}:{attempt}</c>, where the attempt is the job's own
/// persisted <c>RecoveryAttempts</c>. Keying on the job alone would make the second retry a duplicate
/// of the first and drop it silently; keying on a clock would make two recoveries in the same second
/// collide. The counter is the only value that is both stable for one attempt and different for the
/// next.
/// </para>
/// </summary>
public sealed record RetryImportJobCommand(Guid ImportJobId) : ICommand;

/// <summary>Deletes the library files of a work removed from the catalog together with its files.</summary>
public sealed record DeleteWorkFilesCommand(Guid WorkId) : ICommand;
