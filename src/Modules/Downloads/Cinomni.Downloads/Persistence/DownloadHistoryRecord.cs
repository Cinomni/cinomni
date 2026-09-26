using Cinomni.Downloads.Contracts;

namespace Cinomni.Downloads.Persistence;

/// <summary>
/// One immutable line of a download task's transition history (append-only). The trail makes the
/// task's lifecycle observable — every state change, what triggered it, and when — which matters
/// because the task is reconciled against the sidecar across restarts.
/// </summary>
public sealed class DownloadHistoryRecord
{
    public Guid DownloadTaskId { get; init; }

    /// <summary>Monotonic sequence within the task (composite key with <see cref="DownloadTaskId"/>).</summary>
    public int Seq { get; init; }

    public DownloadState FromState { get; init; }

    public DownloadState ToState { get; init; }

    public required string Trigger { get; init; }

    public DateTimeOffset OccurredAt { get; init; }

    public string? Note { get; init; }
}
