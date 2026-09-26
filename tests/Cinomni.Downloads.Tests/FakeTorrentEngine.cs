using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Cinomni.Downloads.Contracts;
using Cinomni.Downloads.Engine;

namespace Cinomni.Downloads.Tests;

/// <summary>
/// In-memory <see cref="ITorrentEngine"/> that scripts the status stream, so the module's
/// orchestration (spine, transitions, persistence, events) is tested deterministically without a
/// live sidecar. The real gRPC transport is exercised by the PoC. Control calls are recorded for
/// assertions.
/// </summary>
internal sealed class FakeTorrentEngine : ITorrentEngine
{
    private readonly ConcurrentDictionary<string, List<TorrentSnapshot>> _streams = new();
    private readonly ConcurrentDictionary<string, IReadOnlyList<TorrentFileInfo>> _files = new();
    private int _adds;

    /// <summary>
    /// The info-hash the <b>next</b> add returns. Each add mints a distinct one from it — a real
    /// engine hands back a different hash for a different torrent, and a fixed one both hid and
    /// blocked every test of what happens when two attempts genuinely share a torrent.
    /// Set <see cref="PinInfoHash"/> to reproduce that collision on purpose.
    /// </summary>
    public string NextInfoHash { get; set; } = "infohash-01";

    /// <summary>When true every add returns <see cref="NextInfoHash"/> — two attempts, one torrent.</summary>
    public bool PinInfoHash { get; set; }

    /// <summary>Every info-hash handed out, in add order.</summary>
    public ConcurrentBag<string> AddedInfoHashes { get; } = [];

    /// <summary>Every add request, in order, so a test can see what rode along with it.</summary>
    public ConcurrentQueue<TorrentAddRequest> Adds { get; } = [];

    public string NextName { get; set; } = "Test.Movie.2024.1080p.BluRay.x264";

    public byte[] ResumeBlob { get; set; } = [1, 2, 3, 4];

    public List<string> Paused { get; } = [];

    public List<string> Resumed { get; } = [];

    public List<(string InfoHash, bool DeleteFiles)> Removed { get; } = [];

    public Dictionary<string, IReadOnlyDictionary<int, FilePriorityLevel>> PrioritiesSet { get; } = [];

    public void ScriptStream(string infoHash, params TorrentSnapshot[] snapshots) =>
        _streams[infoHash] = [.. snapshots];

    /// <summary>Forgets a torrent, as a sidecar that restarted does: status reads answer "unknown".</summary>
    public void Lose(string infoHash) => _streams.TryRemove(infoHash, out _);

    public void SetFiles(string infoHash, params TorrentFileInfo[] files) =>
        _files[infoHash] = files;

    /// <summary>Builds a snapshot with sensible defaults so tests only set what they care about.</summary>
    public static TorrentSnapshot Snapshot(
        string infoHash,
        string state,
        string name = "Test.Movie.2024.1080p.BluRay.x264",
        double progress = 0,
        bool isFinished = false,
        string? error = null,
        long allTimeUpload = 0,
        long allTimeDownload = 0,
        int seedingSeconds = 0,
        bool isPaused = false,
        bool isQueued = false) =>
        new(infoHash, name, state, progress, TotalDone: 0, TotalWanted: 0, DownloadRate: 0, UploadRate: 0,
            NumPeers: 0, NumSeeds: 0, isFinished, error, allTimeUpload, allTimeDownload, seedingSeconds, isPaused,
            isQueued);

    /// <summary>How many times the engine was told to lift its session-wide egress hold.</summary>
    public int HoldOverrides { get; private set; }

    /// <summary>When true every add fails, as an engine that is unreachable does.</summary>
    public bool FailAdds { get; set; }

    public Task OverrideTunnelHoldAsync(CancellationToken cancellationToken = default)
    {
        HoldOverrides++;
        return Task.CompletedTask;
    }

    public Task<TorrentAdded> AddAsync(TorrentAddRequest request, CancellationToken cancellationToken = default)
    {
        if (FailAdds)
        {
            return Task.FromException<TorrentAdded>(new InvalidOperationException("The engine is unreachable."));
        }

        // The first add returns NextInfoHash verbatim so a test can script a stream against it
        // before adding; later adds are suffixed so they are genuinely distinct torrents.
        var ordinal = Interlocked.Increment(ref _adds);
        var infoHash = PinInfoHash || ordinal == 1 ? NextInfoHash : $"{NextInfoHash}-{ordinal}";
        AddedInfoHashes.Add(infoHash);
        Adds.Enqueue(request);
        return Task.FromResult(new TorrentAdded(infoHash, NextName, request.ResumeData is { Length: > 0 }));
    }

    public async IAsyncEnumerable<TorrentSnapshot> StreamStatusAsync(
        string infoHash,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!_streams.TryGetValue(infoHash, out var snapshots))
        {
            yield break;
        }

        foreach (var snapshot in snapshots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return snapshot;
            await Task.Yield();
        }
    }

    public Task<TorrentSnapshot?> GetStatusAsync(string infoHash, CancellationToken cancellationToken = default) =>
        Task.FromResult(_streams.TryGetValue(infoHash, out var s) && s.Count > 0 ? s[^1] : null);

    public Task PauseAsync(string infoHash, CancellationToken cancellationToken = default)
    {
        Paused.Add(infoHash);
        return Task.CompletedTask;
    }

    public Task ResumeAsync(string infoHash, CancellationToken cancellationToken = default)
    {
        Resumed.Add(infoHash);
        return Task.CompletedTask;
    }

    public Task SetFilePrioritiesAsync(
        string infoHash,
        IReadOnlyDictionary<int, FilePriorityLevel> priorities,
        CancellationToken cancellationToken = default)
    {
        PrioritiesSet[infoHash] = priorities;
        return Task.CompletedTask;
    }

    /// <summary>How many of the next file listings fail, as a gRPC call that drops mid-add does.</summary>
    public int FailNextListings { get; set; }

    public Task<IReadOnlyList<TorrentFileInfo>> ListFilesAsync(string infoHash, CancellationToken cancellationToken = default)
    {
        if (FailNextListings > 0)
        {
            FailNextListings--;
            return Task.FromException<IReadOnlyList<TorrentFileInfo>>(new InvalidOperationException("The listing failed."));
        }

        return Task.FromResult(_files.TryGetValue(infoHash, out var files) ? files : []);
    }

    /// <summary>Info-hashes whose resume data the engine fails to hand over, as a blob past the message limit does.</summary>
    public HashSet<string> FailResumeFor { get; } = [];

    public Task<byte[]?> SaveResumeDataAsync(string infoHash, CancellationToken cancellationToken = default) =>
        FailResumeFor.Contains(infoHash)
            ? Task.FromException<byte[]?>(new InvalidOperationException("Resume data could not be transferred."))
            : Task.FromResult<byte[]?>(ResumeBlob);

    public Task RemoveAsync(string infoHash, bool deleteFiles, CancellationToken cancellationToken = default)
    {
        Removed.Add((infoHash, deleteFiles));
        return Task.CompletedTask;
    }

    /// <summary>
    /// What the next egress observation reports. Verified by default so every existing test keeps
    /// describing an installation whose traffic is where it should be.
    /// </summary>
    public TunnelObservation TunnelObservation { get; set; } = new(
        "tun0", TunnelUp: true, DefaultRouteViaTunnel: true, EgressIdentityMatches: true,
        Engine.TunnelObservation.VerifiedReason, "block", SessionHeld: false, DateTimeOffset.UnixEpoch);

    /// <summary>How many times the watch job asked. Lets a test prove a cycle ran, or did not.</summary>
    public int TunnelObservations { get; private set; }

    /// <summary>
    /// When the scripted observation was taken. Null — the default — means "just now", which is what
    /// a working sidecar reports. Setting it is how a test describes a guard that stopped observing:
    /// the answer still arrives and still looks healthy, and only its age says otherwise.
    /// </summary>
    public DateTimeOffset? ObservedAt { get; set; }

    /// <summary>
    /// Makes the observation fail the way a real engine can. An arbitrary exception stands for
    /// everything the transport layer is not obliged to convert — an unhandled error in the sidecar's
    /// own handler, a channel that could not be built — none of which may look like health.
    /// </summary>
    public Exception? ObserveThrows { get; set; }

    /// <summary>Scripts an unverified observation with a given reason (the leak and outage cases).</summary>
    public void ObserveUnverified(string reason) =>
        TunnelObservation = TunnelObservation with
        {
            TunnelUp = false,
            DefaultRouteViaTunnel = false,
            EgressIdentityMatches = false,
            Reason = reason,
        };

    /// <summary>Scripts a verified observation again (the recovery case).</summary>
    public void ObserveVerified() =>
        TunnelObservation = TunnelObservation with
        {
            TunnelUp = true,
            DefaultRouteViaTunnel = true,
            EgressIdentityMatches = true,
            Reason = Engine.TunnelObservation.VerifiedReason,
        };

    public Task<TunnelObservation> ObserveTunnelAsync(CancellationToken cancellationToken = default)
    {
        TunnelObservations += 1;
        if (ObserveThrows is { } failure)
        {
            throw failure;
        }

        return Task.FromResult(TunnelObservation with { ObservedAt = ObservedAt ?? DateTimeOffset.UtcNow });
    }
}
