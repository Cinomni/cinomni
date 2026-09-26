using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Cinomni.Downloads.Contracts;
using Cinomni.Downloads.Engine;

namespace Cinomni.SeriesSlice.Tests;

/// <summary>
/// In-memory <see cref="ITorrentEngine"/> for the acceptance host. Each add mints a distinct info-hash
/// derived from the release it was handed, so "one pack was downloaded once" is a fact this fake can
/// falsify rather than one it fabricates: a fixed hash would make every download look like the same
/// torrent and the info-hash collision guard would appear to work even if it did not.
/// </summary>
internal sealed class FakeTorrentEngine : ITorrentEngine
{
    private readonly ConcurrentDictionary<string, string> _namesByInfoHash = new();
    private readonly ConcurrentDictionary<string, IReadOnlyList<TorrentFileInfo>> _filesByInfoHash = new();
    private readonly ConcurrentDictionary<string, string> _nameByDownloadUrl = new(StringComparer.Ordinal);

    /// <summary>Every info-hash handed out, in add order.</summary>
    public ConcurrentBag<string> AddedInfoHashes { get; } = [];

    /// <summary>The torrent name of a release, i.e. the directory the content lands in when staged.</summary>
    public void ScriptRelease(string downloadUrl, string torrentName) => _nameByDownloadUrl[downloadUrl] = torrentName;

    /// <summary>Every folder the backend told this engine to save into, as a real engine would write there.</summary>
    public System.Collections.Concurrent.ConcurrentBag<string> SavePaths { get; } = [];

    public Task<TorrentAdded> AddAsync(TorrentAddRequest request, CancellationToken cancellationToken = default)
    {
        SavePaths.Add(request.SavePath);
        var name = _nameByDownloadUrl.TryGetValue(request.DownloadUrl, out var scripted) ? scripted : "Unknown.Release";
        var infoHash = InfoHashOf(request.DownloadUrl);
        _namesByInfoHash[infoHash] = name;
        AddedInfoHashes.Add(infoHash);
        return Task.FromResult(new TorrentAdded(infoHash, name, false));
    }

    /// <summary>A snapshot for the torrent behind a release url, so a test never handles hashes itself.</summary>
    public TorrentSnapshot SnapshotFor(string downloadUrl, string state, bool isFinished = false)
    {
        var infoHash = InfoHashOf(downloadUrl);
        var name = _namesByInfoHash.TryGetValue(infoHash, out var known) ? known : "Unknown.Release";
        return new TorrentSnapshot(
            infoHash, name, state, isFinished ? 1.0 : 0.5, TotalDone: 0, TotalWanted: 0, DownloadRate: 0,
            UploadRate: 0, NumPeers: 0, NumSeeds: 0, isFinished, Error: null, AllTimeUpload: 0,
            AllTimeDownload: 0, SeedingSeconds: 0, IsPaused: false);
    }

    /// <summary>
    /// Always empty: the hosted status pump never starts under a plain <c>ServiceProvider</c>, so the
    /// suite feeds snapshots straight into <c>DownloadService.ApplyStatusAsync</c> instead.
    /// </summary>
    public async IAsyncEnumerable<TorrentSnapshot> StreamStatusAsync(
        string infoHash,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();
        yield break;
    }

    public Task<TorrentSnapshot?> GetStatusAsync(string infoHash, CancellationToken cancellationToken = default) =>
        Task.FromResult<TorrentSnapshot?>(null);

    public Task PauseAsync(string infoHash, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task ResumeAsync(string infoHash, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task SetFilePrioritiesAsync(
        string infoHash,
        IReadOnlyDictionary<int, FilePriorityLevel> priorities,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<IReadOnlyList<TorrentFileInfo>> ListFilesAsync(string infoHash, CancellationToken cancellationToken = default) =>
        Task.FromResult(_filesByInfoHash.TryGetValue(infoHash, out var files) ? files : []);

    public Task<byte[]?> SaveResumeDataAsync(string infoHash, CancellationToken cancellationToken = default) =>
        Task.FromResult<byte[]?>(null);

    public Task RemoveAsync(string infoHash, bool deleteFiles, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <summary>
    /// The acceptance host configures no tunnel, so this is never called by the watch job. It answers
    /// "verified" rather than throwing, because a fake that threw would make an unrelated slice fail
    /// for a reason that has nothing to do with the behaviour it is describing.
    /// </summary>
    public Task<TunnelObservation> ObserveTunnelAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new TunnelObservation(
            TunnelDevice: string.Empty,
            TunnelUp: true,
            DefaultRouteViaTunnel: true,
            EgressIdentityMatches: true,
            TunnelObservation.VerifiedReason,
            Policy: string.Empty,
            SessionHeld: false,
            DateTimeOffset.UtcNow));

    /// <summary>Deterministic per release url, which is what makes two distinct releases distinct torrents.</summary>
    private static string InfoHashOf(string downloadUrl) =>
        $"infohash-{Math.Abs(StringComparer.Ordinal.GetHashCode(downloadUrl)):x8}";
}
