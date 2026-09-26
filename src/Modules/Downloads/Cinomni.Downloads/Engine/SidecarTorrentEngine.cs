using Google.Protobuf;
using Grpc.Core;
using Cinomni.Downloads.Contracts;
using Cinomni.Torrent.Grpc;
using Microsoft.Extensions.Logging;

namespace Cinomni.Downloads.Engine;

/// <summary>
/// Production <see cref="ITorrentEngine"/>: talks to the libtorrent sidecar over gRPC.
/// The gRPC client is a shared singleton; this adapter is the hand-written, warnings-as-errors
/// boundary over the generated stub. A magnet is passed straight through; an http(s) link is
/// fetched as a .torrent through an SSRF-hardened client (the link comes from a hostile indexer).
/// </summary>
public sealed class SidecarTorrentEngine(
    TorrentService.TorrentServiceClient client,
    IHttpClientFactory httpClientFactory,
    SidecarOptions options,
    TunnelOptions tunnel,
    ILogger<SidecarTorrentEngine> logger) : ITorrentEngine
{
    /// <summary>Named SSRF-hardened client used to fetch .torrent files from indexer links.</summary>
    public const string TorrentFetchClient = "downloads.torrent-fetch";

    /// <summary>
    /// The metadata key carrying the control-port credential. Lower-case because gRPC normalises
    /// header names, and a key with capitals would simply never match on the other side.
    /// </summary>
    private const string ControlTokenHeader = "x-cinomni-control-token";

    /// <summary>
    /// The call options every request uses: a deadline and, when configured, the control token.
    /// <para>
    /// Built per call rather than cached because the deadline is absolute. Reusing one would mean
    /// every request after the first fifteen seconds of process life was already expired.
    /// </para>
    /// </summary>
    private CallOptions Call(CancellationToken cancellationToken)
    {
        var call = new CallOptions(
            deadline: DateTime.UtcNow.Add(options.CallTimeout),
            cancellationToken: cancellationToken);

        if (options.ControlToken.Length == 0)
        {
            return call;
        }

        // The token is put on the wire and nowhere else: it is never logged, never part of an error
        // message, and never read back out of the options for any other purpose.
        return call.WithHeaders(new Metadata { { ControlTokenHeader, options.ControlToken } });
    }

    public async Task<TorrentAdded> AddAsync(TorrentAddRequest request, CancellationToken cancellationToken = default)
    {
        var grpcRequest = new AddTorrentRequest { SavePath = request.SavePath };
        if (request.ResumeData is { Length: > 0 } resume)
        {
            grpcRequest.ResumeData = ByteString.CopyFrom(resume);
        }
        else if (request.TorrentFile is { Length: > 0 } file)
        {
            grpcRequest.TorrentFile = ByteString.CopyFrom(file);
        }
        else if (request.DownloadUrl.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            grpcRequest.MagnetUri = request.DownloadUrl;
        }
        else
        {
            var http = httpClientFactory.CreateClient(TorrentFetchClient);
            var fetched = await TorrentLinkFetch.FetchAsync(http, request.DownloadUrl, logger, cancellationToken);
            if (fetched.Magnet is { } magnet)
            {
                // The link redirected to a magnet: passed through exactly as one the indexer gave directly.
                grpcRequest.MagnetUri = magnet;
            }
            else
            {
                grpcRequest.TorrentFile = ByteString.CopyFrom(fetched.File);
            }
        }

        var response = await client.AddTorrentAsync(grpcRequest, Call(cancellationToken));
        return new TorrentAdded(response.InfoHash, response.Name, response.Resumed);
    }

    public async IAsyncEnumerable<TorrentSnapshot> StreamStatusAsync(
        string infoHash,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Deliberately without a deadline: this subscription is meant to outlive any call timeout —
        // it stays open for the whole download. The credential still travels with it.
        using var call = client.StreamStatus(new InfoHashRequest { InfoHash = infoHash }, StreamCall(cancellationToken));
        await foreach (var status in call.ResponseStream.ReadAllAsync(cancellationToken))
        {
            yield return Map(status);
        }
    }

    public async Task<TorrentSnapshot?> GetStatusAsync(string infoHash, CancellationToken cancellationToken = default)
    {
        try
        {
            var status = await client.GetStatusAsync(new InfoHashRequest { InfoHash = infoHash }, Call(cancellationToken));
            return Map(status);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task PauseAsync(string infoHash, CancellationToken cancellationToken = default) =>
        await client.PauseTorrentAsync(new InfoHashRequest { InfoHash = infoHash }, Call(cancellationToken));

    public async Task ResumeAsync(string infoHash, CancellationToken cancellationToken = default) =>
        await client.ResumeTorrentAsync(new InfoHashRequest { InfoHash = infoHash }, Call(cancellationToken));

    public async Task OverrideTunnelHoldAsync(CancellationToken cancellationToken = default) =>
        await client.OverrideTunnelHoldAsync(new TunnelHoldOverrideRequest(), Call(cancellationToken));

    public async Task SetFilePrioritiesAsync(
        string infoHash,
        IReadOnlyDictionary<int, FilePriorityLevel> priorities,
        CancellationToken cancellationToken = default)
    {
        var request = new SetFilePrioritiesRequest { InfoHash = infoHash };
        foreach (var (index, level) in priorities)
        {
            request.Priorities.Add(new FilePriorityEntry { FileIndex = index, Priority = LibtorrentPriority.ToWire(level) });
        }

        await client.SetFilePrioritiesAsync(request, Call(cancellationToken));
    }

    public async Task<IReadOnlyList<TorrentFileInfo>> ListFilesAsync(string infoHash, CancellationToken cancellationToken = default)
    {
        try
        {
            var list = await client.ListFilesAsync(new InfoHashRequest { InfoHash = infoHash }, Call(cancellationToken));
            return list.Files
                .Select(f => new TorrentFileInfo(f.Index, f.Path, f.Size, LibtorrentPriority.FromWire(f.Priority)))
                .ToList();
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            return [];
        }
    }

    public async Task<byte[]?> SaveResumeDataAsync(string infoHash, CancellationToken cancellationToken = default)
    {
        try
        {
            var resume = await client.SaveResumeDataAsync(new InfoHashRequest { InfoHash = infoHash }, Call(cancellationToken));
            return resume.Data.ToByteArray();
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task RemoveAsync(string infoHash, bool deleteFiles, CancellationToken cancellationToken = default) =>
        await client.RemoveTorrentAsync(
            new RemoveTorrentRequest { InfoHash = infoHash, DeleteFiles = deleteFiles },
            Call(cancellationToken));

    public async Task<TunnelObservation> ObserveTunnelAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        try
        {
            var observation = await client.GetTunnelHealthAsync(new TunnelHealthRequest(), Call(cancellationToken));
            return new TunnelObservation(
                observation.TunnelDevice,
                observation.TunnelUp,
                observation.DefaultRouteViaTunnel,
                observation.EgressIdentityMatches,
                string.IsNullOrEmpty(observation.Reason) ? TunnelObservation.UnreachableReason : observation.Reason,
                observation.Policy,
                observation.SessionHeld,
                observation.ObservedAtUnix > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(observation.ObservedAtUnix)
                    : now);
        }
        catch (RpcException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Every status means the same thing to a kill-switch: nobody can currently vouch for where
            // the traffic is going. No status is enumerated on purpose. Unimplemented is the rollout
            // case — a sidecar older than this backend — and it is deliberately not treated as healthy,
            // because an old sidecar is also one that never learned to bind to the tunnel;
            // Unauthenticated is the credential being wrong, which is a configuration failure and not a
            // reason to keep downloading; and Unknown is what a gRPC server returns for any unhandled
            // exception inside a handler, which is the single likeliest way this question fails. An
            // allowlist of statuses would let the least anticipated failure be the one that escapes.
            logger.LogWarning(
                "The sidecar could not report its egress path ({Status}); treating it as unverified.",
                ex.StatusCode);
            return TunnelObservation.Unreachable(tunnel.Device, now);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // Not every failure arrives as an RpcException: channel setup, a serialization fault or a
            // handler bug can throw anything at all. The published contract on this method is that it
            // never throws for an engine that is down, absent or too old to answer — because a caller
            // that has to catch cannot fold "I could not ask" into "not verified", and the streak that
            // decides the hold would never be incremented at all.
            logger.LogWarning(ex, "The egress observation failed unexpectedly; treating it as unverified.");
            return TunnelObservation.Unreachable(tunnel.Device, now);
        }
    }

    /// <summary>Call options for a long-lived subscription: the credential, and no deadline.</summary>
    private CallOptions StreamCall(CancellationToken cancellationToken)
    {
        var call = new CallOptions(cancellationToken: cancellationToken);
        return options.ControlToken.Length == 0
            ? call
            : call.WithHeaders(new Metadata { { ControlTokenHeader, options.ControlToken } });
    }

    private static TorrentSnapshot Map(TorrentStatus status) => new(
        status.InfoHash,
        status.Name,
        status.State,
        status.Progress,
        status.TotalDone,
        status.TotalWanted,
        status.DownloadRate,
        status.UploadRate,
        status.NumPeers,
        status.NumSeeds,
        status.IsFinished,
        string.IsNullOrEmpty(status.Error) ? null : status.Error,
        status.AllTimeUpload,
        status.AllTimeDownload,
        status.SeedingSeconds,
        status.IsPaused,
        status.IsQueued);
}
