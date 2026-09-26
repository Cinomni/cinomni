namespace Cinomni.Downloads.Persistence;

/// <summary>
/// Raised when an operation is refused because torrent egress could not be verified: resuming a task
/// that is held, or handing a new release to an engine whose traffic is not going where it should.
/// <para>
/// It is a distinct type rather than an <see cref="InvalidOperationException"/> because the callers
/// answer it differently. The API maps it to a conflict with the reason attached — this is not an
/// illegal transition, it is a refusal an operator is entitled to understand — and a queued command
/// lets it retry, so a short outage costs a delay rather than a lost acquisition.
/// </para>
/// </summary>
public sealed class NetworkHoldException : InvalidOperationException
{
    private NetworkHoldException(string message, Guid downloadTaskId, string reason)
        : base(message)
    {
        DownloadTaskId = downloadTaskId;
        Reason = reason;
    }

    /// <summary>The task that refused, or <see cref="Guid.Empty"/> when no task exists yet.</summary>
    public Guid DownloadTaskId { get; }

    /// <summary>The machine-readable observation behind the hold, for the error envelope.</summary>
    public string Reason { get; }

    /// <summary>An existing task refusing to leave its hold.</summary>
    public NetworkHoldException(Guid downloadTaskId, string reason)
        : this(
            $"Download {downloadTaskId} is held because torrent egress is not verified ({reason}).",
            downloadTaskId,
            reason)
    {
    }

    /// <summary>A release that was never handed to the engine, because the engine is held.</summary>
    public static NetworkHoldException ForNewDownload(string reason) => new(
        $"Torrent egress is not verified ({reason}), so no new download is handed to the engine. "
        + "The acquisition is retried; it starts when the tunnel is carrying traffic again.",
        Guid.Empty,
        reason);
}
