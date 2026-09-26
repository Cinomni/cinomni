using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Cinomni.Downloads.Contracts;
using Microsoft.Extensions.Logging;

namespace Cinomni.Downloads.Engine;

/// <summary>
/// <see cref="ITorrentEngine"/> over qBittorrent's Web API. The sidecar stays the default; this client
/// is used only when the operator points Cinomni at one. It cannot see the kernel route, so a tunnel
/// guard that is on will fail closed rather than treat an external client as verified.
/// </summary>
public sealed class QbittorrentWebEngine(
    HttpClient httpClient,
    IHttpClientFactory httpClientFactory,
    QbittorrentOptions options,
    QbittorrentSession session,
    TunnelOptions tunnel,
    ILogger<QbittorrentWebEngine> logger) : ITorrentEngine, IDownloadEngineProbe
{
    public const string TorrentFetchClient = "qbittorrent-torrent-fetch";
    public const string UnobservedReason = "external-client-unobserved";

    /// <summary>How long one status subscription stays open, matching the sidecar's stream lifetime.</summary>
    private static readonly TimeSpan StreamLifetime = TimeSpan.FromSeconds(30);

    public async Task<TorrentAdded> AddAsync(TorrentAddRequest request, CancellationToken cancellationToken = default)
    {
        var reference = request.DownloadUrl;
        byte[]? torrent = null;
        var hash = TorrentInfoHash.FromMagnet(reference);
        if (request.TorrentFile is { Length: > 0 } supplied)
        {
            // Fetched on the member's behalf already; filtered exactly like one fetched here.
            torrent = TorrentFileTrackers.Filter(supplied);
            hash = torrent is null ? null : TorrentInfoHash.FromTorrent(torrent);
        }
        else if (hash is null)
        {
            var http = httpClientFactory.CreateClient(TorrentFetchClient);
            var fetched = await TorrentLinkFetch.FetchAsync(http, reference, logger, cancellationToken);
            if (fetched.Magnet is { } magnet)
            {
                // The link redirected to a magnet: from here it is treated as one the indexer gave directly.
                reference = magnet;
                hash = TorrentInfoHash.FromMagnet(magnet);
            }
            else
            {
                // Filtered like a magnet's trackers before the client sees it: its own SSRF defence is one
                // Cinomni cannot verify. The info dictionary is untouched, so the hash is the same.
                torrent = TorrentFileTrackers.Filter(fetched.File!);
                hash = torrent is null ? null : TorrentInfoHash.FromTorrent(torrent);
            }
        }

        if (hash is null)
        {
            throw new InvalidOperationException("The download reference did not yield an info-hash.");
        }

        var existing = await GetStatusAsync(hash, cancellationToken);
        if (existing is not null)
        {
            // Adopted only when it saves where Cinomni puts its downloads. A torrent the household added
            // to the client itself is theirs: taking it over would let Cinomni pause, move or delete it.
            if (!await SavesToAsync(hash, request.StagingRoot ?? request.SavePath, cancellationToken))
            {
                throw new InvalidOperationException(
                    "This torrent is already in qBittorrent, saving somewhere Cinomni does not manage; it is left alone.");
            }

            return new TorrentAdded(hash, existing.Name, Resumed: true);
        }

        await AddNewAsync(request, reference, torrent, cancellationToken);
        var added = await GetStatusAsync(hash, cancellationToken);
        return new TorrentAdded(hash, added?.Name ?? string.Empty, Resumed: false);
    }

    public async IAsyncEnumerable<TorrentSnapshot> StreamStatusAsync(
        string infoHash,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Bounded like the sidecar's stream, and for the same reason: the pump watches a limited number of
        // transfers at once and rotates, so a subscription that never ended would keep every transfer
        // after the first few from ever being watched — or seen to finish.
        var endsAt = DateTimeOffset.UtcNow + StreamLifetime;
        while (!cancellationToken.IsCancellationRequested)
        {
            var snapshot = await GetStatusAsync(infoHash, cancellationToken);
            if (snapshot is null)
            {
                yield break;
            }

            yield return snapshot;
            if (snapshot.IsFinished || DateTimeOffset.UtcNow >= endsAt)
            {
                yield break;
            }

            await Task.Delay(options.PollInterval, cancellationToken);
        }
    }

    public async Task<TorrentSnapshot?> GetStatusAsync(string infoHash, CancellationToken cancellationToken = default)
    {
        using var document = await GetJsonAsync($"torrents/info?hashes={Uri.EscapeDataString(infoHash)}", cancellationToken);
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in document.RootElement.EnumerateArray())
        {
            return MapSnapshot(item, infoHash);
        }

        return null;
    }

    public Task PauseAsync(string infoHash, CancellationToken cancellationToken = default) =>
        PostFirstAsync(["torrents/stop", "torrents/pause"], Hashes(infoHash), cancellationToken);

    public Task ResumeAsync(string infoHash, CancellationToken cancellationToken = default) =>
        PostFirstAsync(["torrents/start", "torrents/resume"], Hashes(infoHash), cancellationToken);

    public async Task SetFilePrioritiesAsync(
        string infoHash,
        IReadOnlyDictionary<int, FilePriorityLevel> priorities,
        CancellationToken cancellationToken = default)
    {
        foreach (var group in priorities.GroupBy(pair => (int)pair.Value))
        {
            var ids = string.Join('|', group.Select(pair => pair.Key.ToString(CultureInfo.InvariantCulture)));
            await PostAsync("torrents/filePrio", new Dictionary<string, string>
            {
                ["hash"] = infoHash,
                ["id"] = ids,
                ["priority"] = group.Key.ToString(CultureInfo.InvariantCulture),
            }, cancellationToken);
        }
    }

    public async Task<IReadOnlyList<TorrentFileInfo>> ListFilesAsync(string infoHash, CancellationToken cancellationToken = default)
    {
        using var document = await GetJsonAsync($"torrents/files?hash={Uri.EscapeDataString(infoHash)}", cancellationToken);
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var files = new List<TorrentFileInfo>();
        var index = 0;
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var priority = item.TryGetProperty("priority", out var raw) && raw.TryGetInt32(out var value)
                ? (FilePriorityLevel)value
                : FilePriorityLevel.Normal;
            files.Add(new TorrentFileInfo(
                index++,
                item.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : string.Empty,
                item.TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes) ? bytes : 0,
                priority));
        }

        return files;
    }

    public Task<byte[]?> SaveResumeDataAsync(string infoHash, CancellationToken cancellationToken = default) =>
        Task.FromResult<byte[]?>(null);

    public Task RemoveAsync(string infoHash, bool deleteFiles, CancellationToken cancellationToken = default) =>
        PostAsync("torrents/delete", new Dictionary<string, string>
        {
            ["hashes"] = infoHash,
            ["deleteFiles"] = deleteFiles ? "true" : "false",
        }, cancellationToken);

    public Task<TunnelObservation> ObserveTunnelAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new TunnelObservation(
            tunnel.Device,
            false,
            false,
            false,
            UnobservedReason,
            string.Empty,
            false,
            DateTimeOffset.UtcNow));

    private async Task AddNewAsync(TorrentAddRequest request, string reference, byte[]? torrent, CancellationToken cancellationToken)
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(request.SavePath), "savepath");
        // Automatic torrent management would let the client pick its own directory and ignore the save
        // path — the staging area Import reads from, and the one Cinomni recognises its torrents by.
        content.Add(new StringContent("false"), "autoTMM");
        if (torrent is null)
        {
            // Never the link as it arrived: qBittorrent splits "urls" on line breaks, and a feed could
            // hide a second torrent or an internal URL behind one. Rebuilt, it is one magnet and nothing else.
            var magnet = MagnetLink.Canonical(reference)
                ?? throw new InvalidOperationException("The download reference is not a magnet with an info-hash.");
            content.Add(new StringContent(magnet), "urls");
        }
        else
        {
            var file = new ByteArrayContent(torrent);
            file.Headers.ContentType = new MediaTypeHeaderValue("application/x-bittorrent");
            content.Add(file, "torrents", "download.torrent");
        }

        await SendAsync(HttpMethod.Post, "torrents/add", content, cancellationToken);
    }

    /// <summary>Whether the client's torrent with this hash saves into <paramref name="savePath"/> or a folder inside it.</summary>
    private async Task<bool> SavesToAsync(string infoHash, string savePath, CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync($"torrents/info?hashes={Uri.EscapeDataString(infoHash)}", cancellationToken);
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var item in document.RootElement.EnumerateArray())
        {
            return item.TryGetProperty("save_path", out var saved) && saved.GetString() is { } path
                && IsSameOrInside(NormalizedPath(path), NormalizedPath(savePath));
        }

        return false;
    }

    /// <summary>
    /// The staging area itself, or a task's folder inside it: each task now downloads into a folder of
    /// its own, so a torrent Cinomni added saves into a subfolder of the root, not into the root. A root
    /// of <c>/</c> contains everything, so it adopts nothing: every torrent in the household's client
    /// would otherwise be Cinomni's to pause, move or delete.
    /// </summary>
    private static bool IsSameOrInside(string path, string root)
    {
        var trimmed = root.TrimEnd('/');
        if (trimmed.Length == 0)
        {
            return false;
        }

        return string.Equals(path, trimmed, StringComparison.Ordinal)
            || path.StartsWith(trimmed + "/", StringComparison.Ordinal);
    }

    /// <summary>
    /// A path as text, spelled one way: forward slashes, no empty or "." segments, ".." applied, no
    /// trailing separator. The client may report the save path spelled differently from how it was
    /// sent — it cleans paths — and the same directory must compare equal either way.
    /// </summary>
    internal static string NormalizedPath(string path)
    {
        var rooted = path.StartsWith('/') || path.StartsWith('\\');
        var segments = new List<string>();
        foreach (var segment in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == ".." && segments.Count > 0 && segments[^1] != "..")
            {
                segments.RemoveAt(segments.Count - 1);
                continue;
            }

            segments.Add(segment);
        }

        return (rooted ? "/" : string.Empty) + string.Join('/', segments);
    }

    private async Task<JsonDocument?> GetJsonAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, path, null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
    }

    private Task PostAsync(string path, Dictionary<string, string> fields, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, path, new FormUrlEncodedContent(fields), cancellationToken);

    private async Task PostFirstAsync(string[] paths, Dictionary<string, string> fields, CancellationToken cancellationToken)
    {
        foreach (var path in paths)
        {
            using var response = await SendAsync(HttpMethod.Post, path, new FormUrlEncodedContent(fields), cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                continue;
            }

            response.EnsureSuccessStatusCode();
            return;
        }
    }

    /// <summary>
    /// An authenticated read of the client's version: it proves the Web API is up and that the
    /// configured account can sign in, and it changes nothing. Reachability of the socket alone would
    /// report a client that refuses every add as healthy.
    /// </summary>
    public async Task<bool> IsReachableAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(timeout);

        try
        {
            using var response = await SendAsync(HttpMethod.Get, "app/version", null, budget.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (exception is not OperationCanceledException
                                          || !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        HttpContent? content,
        CancellationToken cancellationToken)
    {
        var response = await SendOnceAsync(method, path, content, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Forbidden)
        {
            return response;
        }

        response.Dispose();
        session.Sid = null;
        throw new HttpRequestException("qBittorrent rejected the session.");
    }

    private async Task<HttpResponseMessage> SendOnceAsync(
        HttpMethod method,
        string path,
        HttpContent? content,
        CancellationToken cancellationToken)
    {
        await EnsureLoginAsync(cancellationToken);
        using var request = new HttpRequestMessage(method, path) { Content = content };
        if (session.Sid is not null)
        {
            request.Headers.Add("Cookie", "SID=" + session.Sid);
        }

        return await httpClient.SendAsync(request, cancellationToken);
    }

    private async Task EnsureLoginAsync(CancellationToken cancellationToken)
    {
        if (session.Sid is not null)
        {
            return;
        }

        await session.Gate.WaitAsync(cancellationToken);
        try
        {
            if (session.Sid is not null)
            {
                return;
            }

            if (session.IsHeldOff)
            {
                throw new InvalidOperationException("qBittorrent refused the login recently; not signing in again yet.");
            }

            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = options.Username,
                ["password"] = options.Password,
            });
            using var response = await httpClient.PostAsync("auth/login", content, cancellationToken);
            // A 403 here is the client's ban on this address, which another attempt only renews.
            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                session.RecordRefusal();
                throw new InvalidOperationException("qBittorrent refused the login.");
            }

            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!body.Contains("Ok", StringComparison.Ordinal))
            {
                session.RecordRefusal();
                throw new InvalidOperationException("qBittorrent refused the login.");
            }

            var sid = response.Headers.TryGetValues("Set-Cookie", out var cookies)
                ? cookies.Select(cookie => cookie.Split(';')[0]).FirstOrDefault(cookie => cookie.StartsWith("SID=", StringComparison.Ordinal))?[4..]
                : null;
            if (string.IsNullOrEmpty(sid))
            {
                throw new InvalidOperationException("qBittorrent login did not return a session.");
            }

            session.Sid = sid;
        }
        finally
        {
            session.Gate.Release();
        }
    }

    private static Dictionary<string, string> Hashes(string infoHash) => new() { ["hashes"] = infoHash };

    /// <summary>
    /// Whether qBittorrent knows the torrent's metadata. Before it does, the name it reports is the
    /// magnet's <c>dn=</c> (or the hash), which is not the folder the torrent writes. Newer clients say
    /// so outright; older ones only through the metadata states and a size that is not known yet.
    /// </summary>
    private static bool HasMetadata(JsonElement item, string? state)
    {
        if (item.TryGetProperty("has_metadata", out var flag) && flag.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return flag.GetBoolean();
        }

        if (state is "metaDL" or "forcedMetaDL")
        {
            return false;
        }

        return !(item.TryGetProperty("total_size", out var size) && size.TryGetInt64(out var bytes) && bytes <= 0);
    }

    private static TorrentSnapshot MapSnapshot(JsonElement item, string infoHash)
    {
        var progress = item.TryGetProperty("progress", out var progressValue) && progressValue.TryGetDouble(out var fraction)
            ? fraction
            : 0;
        var state = item.TryGetProperty("state", out var stateValue) ? stateValue.GetString() : null;
        var mapped = QbittorrentState.Map(state, progress);
        long Long(string name) => item.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : 0;
        int Int(string name) => item.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;
        return new TorrentSnapshot(
            infoHash,
            HasMetadata(item, state) && item.TryGetProperty("name", out var name)
                ? name.GetString() ?? string.Empty
                : string.Empty,
            mapped.State,
            progress,
            Long("downloaded"),
            Long("size"),
            Long("dlspeed"),
            Long("upspeed"),
            Int("num_leechs"),
            Int("num_seeds"),
            mapped.IsFinished,
            mapped.Error,
            Long("uploaded"),
            Long("downloaded"),
            Int("seeding_time"),
            mapped.IsPaused,
            mapped.IsQueued);
    }
}
