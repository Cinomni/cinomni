namespace Cinomni.Downloads.Persistence;

/// <summary>
/// Raised when an operator asks a task for a control transition its state does not allow — pausing a
/// download that is already seeding, resuming one that finished. Distinct from a bare
/// <see cref="InvalidOperationException"/> so the API can answer it as the conflict it is, instead of
/// the fault an unhandled exception becomes.
/// </summary>
public sealed class DownloadStateConflictException : InvalidOperationException
{
    public DownloadStateConflictException(Guid downloadTaskId, string reason)
        : base($"Download {downloadTaskId} cannot do that: {reason}.")
    {
        DownloadTaskId = downloadTaskId;
        Reason = reason;
    }

    /// <summary>The task that refused.</summary>
    public Guid DownloadTaskId { get; }

    /// <summary>Why, in words fit for the error envelope: it names the state, never a path or a hash.</summary>
    public string Reason { get; }
}
