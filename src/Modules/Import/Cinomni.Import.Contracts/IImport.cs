namespace Cinomni.Import.Contracts;

/// <summary>
/// Write surface of the Import module. Processing a completed download is idempotent: keyed by the
/// download task, a redelivered <c>DownloadCompleted</c> (or a command retry) reuses the existing
/// job and resumes it from its last confirmed step rather than importing twice (case #15).
/// </summary>
public interface IImportProcessor
{
    /// <summary>
    /// Lands a completed download into the library: match → decide → operate (recoverable file
    /// operations) → probe (ffprobe) → register the asset, emitting <c>ImportRequested</c> and then
    /// <c>ImportCompleted</c>+<c>MediaAvailable</c> (or <c>ImportFailed</c>). No-op if the download
    /// already has a terminal job.
    /// </summary>
    /// <param name="unitIds">
    /// The catalog units the download was meant to satisfy, echoed from <c>DownloadCompleted</c>.
    /// Optional and trailing so existing callers are unaffected; when omitted the job's work id is
    /// the unit, which is the movie case.
    /// </param>
    Task ProcessCompletedDownloadAsync(
        Guid downloadTaskId,
        Guid intentId,
        Guid attemptId,
        Guid workId,
        Guid targetId,
        string contentPath,
        IReadOnlyList<Guid>? unitIds = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Read model of import jobs and their file-operation/history trail.</summary>
public interface IImportQuery
{
    Task<IReadOnlyList<ImportJobSummary>> ListAsync(CancellationToken cancellationToken = default);

    Task<ImportJobDetail?> GetAsync(ImportJobId jobId, CancellationToken cancellationToken = default);
}
