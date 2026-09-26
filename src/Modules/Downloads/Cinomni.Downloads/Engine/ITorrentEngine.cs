using Cinomni.Downloads.Contracts;

namespace Cinomni.Downloads.Engine;

/// <summary>
/// What to add to the engine: an indexer reference (magnet or .torrent link) or a resume blob.
/// <see cref="SavePath"/> is the task's own folder; <see cref="StagingRoot"/>, when given, is the area
/// every task folder lives in, which is what an engine that finds the torrent already present checks it
/// against before treating it as Cinomni's. <see cref="TorrentFile"/>, when given, is the .torrent already
/// fetched on the member's behalf (a private tracker's link cannot be fetched anonymously); the engine
/// adds it as it is instead of fetching <see cref="DownloadUrl"/>.
/// </summary>
public sealed record TorrentAddRequest(
    string DownloadUrl, string SavePath, byte[]? ResumeData, string? StagingRoot = null, byte[]? TorrentFile = null);

/// <summary>
/// The engine's answer to an add: the assigned info-hash, name, and whether it resumed from checkpoint.
/// Here and on <see cref="TorrentSnapshot"/>, <c>Name</c> is empty until the torrent's metadata is
/// known: before that the only name a magnet has is its <c>dn=</c>, which the swarm need not honour
/// and which is not the folder the torrent writes to.
/// </summary>
public sealed record TorrentAdded(string InfoHash, string Name, bool Resumed);

/// <summary>
/// A point-in-time view of a torrent from the engine — the .NET analogue of libtorrent's
/// <c>torrent_status</c>, carried by the status stream. <see cref="State"/> is the raw engine state
/// name (e.g. <c>downloading_metadata</c>, <c>checking_files</c>, <c>downloading</c>, <c>seeding</c>),
/// which the application maps onto the <see cref="DownloadState"/> machine.
/// </summary>
public sealed record TorrentSnapshot(
    string InfoHash,
    string Name,
    string State,
    double Progress,
    long TotalDone,
    long TotalWanted,
    long DownloadRate,
    long UploadRate,
    int NumPeers,
    int NumSeeds,
    bool IsFinished,
    string? Error,
    long AllTimeUpload,
    long AllTimeDownload,
    int SeedingSeconds,
    bool IsPaused,
    bool IsQueued = false);

/// <summary>One file in a torrent as the engine reports it.</summary>
public sealed record TorrentFileInfo(int Index, string Path, long Size, FilePriorityLevel Priority);

/// <summary>
/// Port to the libtorrent sidecar: the engine runs as a separate process, so a native
/// crash never takes down the backend. The production adapter speaks gRPC (<see cref="SidecarTorrentEngine"/>);
/// tests substitute an in-memory fake that scripts the status stream. Implementations must tolerate
/// being called for an unknown info-hash (the sidecar may have restarted) — read operations return
/// null/empty, control operations are no-ops or throw a transient error the caller can retry.
/// </summary>
public interface ITorrentEngine
{
    Task<TorrentAdded> AddAsync(TorrentAddRequest request, CancellationToken cancellationToken = default);

    IAsyncEnumerable<TorrentSnapshot> StreamStatusAsync(string infoHash, CancellationToken cancellationToken = default);

    Task<TorrentSnapshot?> GetStatusAsync(string infoHash, CancellationToken cancellationToken = default);

    Task PauseAsync(string infoHash, CancellationToken cancellationToken = default);

    Task ResumeAsync(string infoHash, CancellationToken cancellationToken = default);

    Task SetFilePrioritiesAsync(
        string infoHash,
        IReadOnlyDictionary<int, FilePriorityLevel> priorities,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TorrentFileInfo>> ListFilesAsync(string infoHash, CancellationToken cancellationToken = default);

    Task<byte[]?> SaveResumeDataAsync(string infoHash, CancellationToken cancellationToken = default);

    Task RemoveAsync(string infoHash, bool deleteFiles, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks the engine where its traffic is actually going. Never throws for an engine that is down,
    /// absent or too old to answer: it returns <see cref="TunnelObservation.Unreachable"/>, because
    /// "I could not ask" and "everything is fine" must never be the same answer to a kill-switch.
    /// </summary>
    Task<TunnelObservation> ObserveTunnelAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The engine's half of the operator's override under <c>PauseAndAlert</c>: lifts its own
    /// session-wide egress hold for the rest of the current outage, so the download the operator
    /// resumed can actually move. An engine without such a hold has nothing to lift.
    /// </summary>
    Task OverrideTunnelHoldAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
