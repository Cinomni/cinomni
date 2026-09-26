using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using Cinomni.Downloads.Contracts;
using Cinomni.Downloads.Engine;

namespace Cinomni.Recovery.Tests.Fakes;

/// <summary>One add the engine was asked for, kept so a restart's re-add can be inspected.</summary>
/// <param name="DownloadUrl">
/// The reference the caller passed. Recorded, but deliberately <b>not</b> what identifies the torrent
/// once a checkpoint rides along — see <see cref="RecoveryTorrentEngine.AddAsync"/>.
/// </param>
/// <param name="ResumeData">The checkpoint that rode along, or null when there was none.</param>
/// <param name="ResolvedInfoHash">The identity the engine resolved the request to.</param>
public sealed record RecordedAdd(string DownloadUrl, byte[]? ResumeData, string ResolvedInfoHash)
{
    /// <summary>Whether this add carried a checkpoint — the whole point of scenario 14.</summary>
    public bool CarriedCheckpoint => ResumeData is { Length: > 0 };
}

/// <summary>
/// In-memory <see cref="ITorrentEngine"/> for the recovery suite. It deliberately outlives the
/// service provider that uses it, because the sidecar is a separate process and genuinely
/// survives a backend restart — a fake rebuilt with the provider would make every restart look like a
/// sidecar restart too, and would hide the case the checkpoint exists for.
/// <para>
/// Two switches the module suites do not need: <see cref="Unreachable"/> makes every call fail the
/// way a transport error would, and <see cref="ForgetEverything"/> models a sidecar that restarted
/// alongside the backend and no longer holds any torrent.
/// </para>
/// </summary>
internal sealed class RecoveryTorrentEngine : ITorrentEngine
{
    private readonly ConcurrentDictionary<string, string> _nameByDownloadUrl = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _nameByInfoHash = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TorrentSnapshot> _statusByInfoHash = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IReadOnlyList<TorrentFileInfo>> _filesByInfoHash =
        new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, byte> _held = new(StringComparer.Ordinal);

    /// <summary>info-hash → whether the torrent is transferring: an add starts it, pause and resume move it.</summary>
    private readonly ConcurrentDictionary<string, bool> _running = new(StringComparer.Ordinal);

    /// <summary>
    /// Runs inside an add, after the request is accepted and before the torrent is registered — the
    /// moment a concurrent pause, hold or removal can commit while a recovery is mid-flight. Lets a test
    /// place that interleaving exactly instead of hoping for it.
    /// </summary>
    public Func<Task>? DuringAdd { get; set; }

    /// <summary>Every add, in order, with the checkpoint it carried.</summary>
    public ConcurrentQueue<RecordedAdd> Adds { get; } = [];

    /// <summary>Every info-hash the engine was told to stop.</summary>
    public ConcurrentQueue<string> Paused { get; } = [];

    /// <summary>Every info-hash the engine was told to start again.</summary>
    public ConcurrentQueue<string> Resumed { get; } = [];

    /// <summary>
    /// What the sidecar's <c>.fastresume</c> blob is, as far as this suite is concerned: a marker and
    /// the torrent's own identity. Real resume data round-trips the swarm-supplied <c>info</c>
    /// dictionary, which is exactly why the adapter stops sending the download URL once a blob exists —
    /// so the blob has to be able to carry an identity here, or the suite could never observe one that
    /// disagrees with the task.
    /// </summary>
    private static readonly byte[] CheckpointMarker = [0x0C, 0x1A, 0x0E, 0x05];

    /// <summary>The checkpoint the engine hands back for a torrent it holds.</summary>
    public static byte[] CheckpointFor(string downloadUrl) =>
        [.. CheckpointMarker, .. Encoding.UTF8.GetBytes(InfoHashOf(downloadUrl))];

    /// <summary>When true every call fails, the way an engine that is not there does.</summary>
    public bool Unreachable { get; set; }

    /// <summary>
    /// When true every add fails while everything else answers — a link the indexer no longer serves, a
    /// torrent the engine will not accept. The release never becomes a download.
    /// </summary>
    public bool RefuseAdds { get; set; }

    /// <summary>
    /// Whether the engine can vouch for where its traffic leaves. False models the case the
    /// <see cref="Unreachable"/> switch cannot: a sidecar that answers every call perfectly well and
    /// reports that egress is <b>not</b> going through the tunnel — a VPN container that failed to
    /// come up, a route that never appeared, an identity that does not match.
    /// </summary>
    public bool EgressVerified { get; set; } = true;

    /// <summary>Drops everything the engine holds, as a sidecar restart would.</summary>
    public void ForgetEverything()
    {
        _nameByInfoHash.Clear();
        _filesByInfoHash.Clear();
        _statusByInfoHash.Clear();
        _held.Clear();
        _running.Clear();
    }

    /// <summary>True when the engine holds the torrent behind a release and it is transferring.</summary>
    public bool IsRunning(string downloadUrl) =>
        _held.ContainsKey(InfoHashOf(downloadUrl)) && _running.GetValueOrDefault(InfoHashOf(downloadUrl));

    /// <summary>
    /// Names the content a release stages under, so a test never handles an info-hash itself. A
    /// release that is never scripted is a magnet whose metadata has not resolved: the engine accepts
    /// it and can answer with no name at all, which is the state the layout is missing from.
    /// </summary>
    public void ScriptRelease(string downloadUrl, string torrentName) =>
        _nameByDownloadUrl[downloadUrl] = torrentName;

    /// <summary>
    /// The engine learned a torrent's name and layout — a magnet whose metadata resolved. Recorded
    /// against the info-hash, because that is what the engine knows a torrent by once it holds one,
    /// and the only thing it is given on a re-add from a checkpoint.
    /// </summary>
    public void ResolveMetadata(string downloadUrl, string torrentName, params TorrentFileInfo[] files)
    {
        var infoHash = InfoHashOf(downloadUrl);
        _nameByInfoHash[infoHash] = torrentName;
        _filesByInfoHash[infoHash] = files;
    }

    /// <summary>The info-hash a release resolves to. Deterministic, so two releases stay two torrents.</summary>
    public static string InfoHashOf(string downloadUrl) =>
        $"infohash-{Math.Abs(StringComparer.Ordinal.GetHashCode(downloadUrl)):x8}";

    /// <summary>A snapshot for the torrent behind a release, in the state a test wants to describe.</summary>
    public TorrentSnapshot SnapshotFor(string downloadUrl, string state, bool isFinished = false, string? error = null)
    {
        var infoHash = InfoHashOf(downloadUrl);
        var name = _nameByInfoHash.TryGetValue(infoHash, out var known)
            ? known
            : _nameByDownloadUrl.GetValueOrDefault(downloadUrl, string.Empty);
        return new TorrentSnapshot(
            infoHash, name, state, isFinished ? 1.0 : 0.5, TotalDone: 0, TotalWanted: 0, DownloadRate: 0,
            UploadRate: 0, NumPeers: 0, NumSeeds: 0, isFinished, error, AllTimeUpload: 0,
            AllTimeDownload: 0, SeedingSeconds: 0, IsPaused: false);
    }

    /// <summary>Makes the engine answer this snapshot to <see cref="GetStatusAsync"/> from now on.</summary>
    public void ScriptStatus(TorrentSnapshot snapshot) => _statusByInfoHash[snapshot.InfoHash] = snapshot;

    /// <summary>True when the engine currently holds the torrent behind a release.</summary>
    public bool Holds(string downloadUrl) => _held.ContainsKey(InfoHashOf(downloadUrl));

    /// <summary>
    /// Adds a torrent the way the production adapter does, which is the part worth copying: once
    /// resume data is supplied the download URL is <b>not sent at all</b>
    /// (<c>SidecarTorrentEngine.AddAsync</c> sets either the blob, or the magnet, or the fetched
    /// .torrent — never a blob and a reference), so on the recovery path the torrent's identity comes
    /// from the checkpoint and from nothing else.
    /// <para>
    /// A fake that resolved identity from the URL while resuming from the blob would be more capable
    /// than the transport it stands for, and no test written against it could ever observe a checkpoint
    /// that names a different torrent than the task it was loaded from.
    /// </para>
    /// </summary>
    /// <summary>Every folder the backend told this engine to save into, as a real engine would write there.</summary>
    public System.Collections.Concurrent.ConcurrentBag<string> SavePaths { get; } = [];

    public async Task<TorrentAdded> AddAsync(TorrentAddRequest request, CancellationToken cancellationToken = default)
    {
        RefuseWhileUnreachable();
        if (RefuseAdds)
        {
            throw new HttpRequestException("Response status code does not indicate success: 404 (Not Found).");
        }

        SavePaths.Add(request.SavePath);
        var resumed = request.ResumeData is { Length: > 0 };
        var infoHash = resumed ? IdentityIn(request.ResumeData!) : InfoHashOf(request.DownloadUrl);
        var name = _nameByInfoHash.TryGetValue(infoHash, out var known)
            ? known
            : _nameByDownloadUrl.GetValueOrDefault(request.DownloadUrl, string.Empty);

        Adds.Enqueue(new RecordedAdd(request.DownloadUrl, request.ResumeData, infoHash));
        if (infoHash.Length == 0)
        {
            // Bytes the engine cannot read as a torrent leave it with no identity to answer with —
            // which the caller has to refuse rather than write over the task's own.
            return new TorrentAdded(string.Empty, string.Empty, false);
        }

        if (DuringAdd is { } interleave)
        {
            await interleave();
        }

        _nameByInfoHash[infoHash] = name;
        _held[infoHash] = 0;
        _running[infoHash] = true;

        // Exactly what libtorrent reports: the add resumed when the caller supplied resume data it
        // could use, and started from nothing otherwise.
        return new TorrentAdded(infoHash, name, resumed);
    }

    /// <summary>The torrent a checkpoint names, or empty when the bytes are not a checkpoint at all.</summary>
    private static string IdentityIn(byte[] resumeData) =>
        resumeData.Length > CheckpointMarker.Length
        && resumeData.Take(CheckpointMarker.Length).SequenceEqual(CheckpointMarker)
            ? Encoding.UTF8.GetString(resumeData, CheckpointMarker.Length, resumeData.Length - CheckpointMarker.Length)
            : string.Empty;

    /// <summary>
    /// Always empty. The hosted status pump never starts under a plain service provider, so the suite
    /// feeds snapshots straight into the application instead.
    /// </summary>
    public async IAsyncEnumerable<TorrentSnapshot> StreamStatusAsync(
        string infoHash,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();
        yield break;
    }

    public Task<TorrentSnapshot?> GetStatusAsync(string infoHash, CancellationToken cancellationToken = default)
    {
        RefuseWhileUnreachable();
        return Task.FromResult(_statusByInfoHash.GetValueOrDefault(infoHash));
    }

    public Task PauseAsync(string infoHash, CancellationToken cancellationToken = default)
    {
        RefuseWhileUnreachable();
        Paused.Enqueue(infoHash);
        _running[infoHash] = false;
        return Task.CompletedTask;
    }

    public Task ResumeAsync(string infoHash, CancellationToken cancellationToken = default)
    {
        RefuseWhileUnreachable();
        Resumed.Enqueue(infoHash);
        _running[infoHash] = true;
        return Task.CompletedTask;
    }

    public Task SetFilePrioritiesAsync(
        string infoHash,
        IReadOnlyDictionary<int, FilePriorityLevel> priorities,
        CancellationToken cancellationToken = default)
    {
        RefuseWhileUnreachable();
        return Task.CompletedTask;
    }

    /// <summary>The layout of a torrent whose metadata has resolved; empty while it has not.</summary>
    public Task<IReadOnlyList<TorrentFileInfo>> ListFilesAsync(
        string infoHash,
        CancellationToken cancellationToken = default)
    {
        RefuseWhileUnreachable();
        return Task.FromResult(_filesByInfoHash.GetValueOrDefault(infoHash, []));
    }

    public Task<byte[]?> SaveResumeDataAsync(string infoHash, CancellationToken cancellationToken = default)
    {
        RefuseWhileUnreachable();

        // Only a torrent the engine actually holds has a checkpoint, and the checkpoint names it.
        return Task.FromResult<byte[]?>(
            _held.ContainsKey(infoHash) ? [.. CheckpointMarker, .. Encoding.UTF8.GetBytes(infoHash)] : null);
    }

    public Task RemoveAsync(string infoHash, bool deleteFiles, CancellationToken cancellationToken = default)
    {
        RefuseWhileUnreachable();
        _held.TryRemove(infoHash, out _);
        _running.TryRemove(infoHash, out _);
        return Task.CompletedTask;
    }

    /// <summary>
    /// The engine contract promises this never throws, so an unreachable engine reports itself as
    /// unverified rather than as an exception the guard would have to interpret.
    /// </summary>
    public Task<TunnelObservation> ObserveTunnelAsync(CancellationToken cancellationToken = default)
    {
        if (Unreachable)
        {
            return Task.FromResult(TunnelObservation.Unreachable("tun0", DateTimeOffset.UtcNow));
        }

        return Task.FromResult(EgressVerified
            ? new TunnelObservation(
                "tun0", TunnelUp: true, DefaultRouteViaTunnel: true, EgressIdentityMatches: true,
                TunnelObservation.VerifiedReason, "block", SessionHeld: false, DateTimeOffset.UtcNow)
            : new TunnelObservation(
                "tun0", TunnelUp: false, DefaultRouteViaTunnel: false, EgressIdentityMatches: false,
                "tunnel-device-missing", "block", SessionHeld: true, DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// The shape a gRPC transport failure reaches the application as. An arbitrary exception on
    /// purpose: the module must not depend on recognising a particular one to stay safe.
    /// </summary>
    private void RefuseWhileUnreachable()
    {
        if (Unreachable)
        {
            throw new InvalidOperationException("The torrent engine is unreachable.");
        }
    }
}
