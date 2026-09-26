namespace Cinomni.Downloads.Contracts;

/// <summary>
/// Read model of download tasks and their file/history trail. The write side is driven by
/// integration events (<c>DownloadQueued</c>) and the sidecar status stream, not by a public
/// command interface — Downloads owns the download, no other module controls it and
/// no caller reaches into the engine).
/// </summary>
public interface IDownloadQuery
{
    Task<IReadOnlyList<DownloadTaskSummary>> ListActiveAsync(CancellationToken cancellationToken = default);

    Task<DownloadTaskDetail?> GetAsync(DownloadTaskId id, CancellationToken cancellationToken = default);

    /// <summary>
    /// The installation's torrent egress state: whether a tunnel is configured, whether traffic was
    /// last verified inside it, and what the policy did about it. Answers from what was persisted at
    /// the last observation, so it costs one row and is safe to poll.
    /// </summary>
    Task<TunnelEgressStatus> GetTunnelStatusAsync(CancellationToken cancellationToken = default);
}
