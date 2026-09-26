using System.Collections.Concurrent;
using Cinomni.Downloads.Application;
using Cinomni.Downloads.Contracts;
using Cinomni.Downloads.Engine;
using Cinomni.Downloads.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cinomni.Downloads.Streaming;

/// <summary>
/// Live status pump (the analogue of libtorrent's alert stream): for each streamable task it keeps
/// a server-streaming subscription open on the sidecar and feeds every snapshot into
/// <see cref="DownloadService.ApplyStatusAsync"/>, which advances the state machine and emits the
/// integration events. This is the first hosted service outside the platform kernel — it follows
/// the same shape as Operations' background services (scope-per-unit, resolve a scoped core).
/// A task's stream closes when the sidecar finishes it; from there the periodic checkpoint job
/// takes over (seeding + resume data). Streams also close after a bounded lifetime on the sidecar's
/// side, and at most <see cref="MaxConcurrentStreams"/> are open at once, so with many transfers in
/// flight each is watched in turn rather than all of them holding a sidecar thread for good.
/// </summary>
public sealed class DownloadStreamPump(
    IServiceScopeFactory scopeFactory,
    ILogger<DownloadStreamPump> logger) : BackgroundService
{
    private static readonly TimeSpan DiscoveryInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long a task's stream waits before being re-subscribed after ending without producing a
    /// single snapshot. Doubling up to the cap turns a sidecar that is gone — the normal state during
    /// a tunnel outage — from a warning every two seconds into a handful over an hour. The log has to
    /// stay readable during the incident it is describing.
    /// </summary>
    private static readonly TimeSpan MinimumRetryDelay = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Subscriptions open at once. The sidecar gives status streams a fixed share of its worker threads
    /// and ends each one after a bounded lifetime, so this stays below that share and the streams
    /// rotate: when one ends, the task watched least recently goes next. Without a cap every in-flight
    /// torrent held a sidecar thread for good, and past a dozen or so the calls that matter — adds,
    /// pauses, checkpoints, the egress question — stopped being answered at all.
    /// </summary>
    internal const int MaxConcurrentStreams = 8;

    /// <summary>
    /// Consecutive subscriptions that must end without a single snapshot before the engine is asked
    /// whether it still holds the torrent. More than one, so a removal committing in the moment between
    /// the engine letting go and the row saying so is never mistaken for a lost transfer.
    /// </summary>
    private const int EmptyStreamsBeforeRecovery = 2;

    private static readonly DownloadState[] StreamableStates =
    [
        DownloadState.Queued, DownloadState.ResolvingMetadata,
        DownloadState.Checking, DownloadState.Downloading,
    ];

    /// <summary>info-hash → the running subscription task, so each task is streamed at most once.</summary>
    private readonly ConcurrentDictionary<string, Task> _streams = new();

    /// <summary>info-hash → how long to wait before re-subscribing. Reset by any snapshot that arrives.</summary>
    private readonly ConcurrentDictionary<string, TimeSpan> _retryDelays = new();

    /// <summary>info-hash → when its last subscription ended, which decides whose turn is next.</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastStreamed = new();

    /// <summary>info-hash → subscriptions in a row that produced nothing. Reset by any snapshot.</summary>
    private readonly ConcurrentDictionary<string, int> _emptyStreams = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DiscoverAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Download stream discovery failed.");
            }

            await Task.Delay(DiscoveryInterval, stoppingToken);
        }
    }

    private async Task DiscoverAsync(CancellationToken stoppingToken)
    {
        foreach (var (key, task) in _streams)
        {
            if (task.IsCompleted)
            {
                _streams.TryRemove(key, out _);
            }
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DownloadsDbContext>();
        var infoHashes = await dbContext.Tasks
            .AsNoTracking()
            .Where(t => StreamableStates.Contains(t.State) && t.InfoHash != null)
            .Select(t => t.InfoHash!)
            .ToListAsync(stoppingToken);

        // A task that stopped being streamable — completed, removed, or held while the tunnel is not
        // carrying its traffic — takes its backoff with it. Otherwise the map would keep one entry per
        // info-hash this process ever streamed, for the life of the process.
        var streamable = infoHashes.ToHashSet(StringComparer.Ordinal);
        Forget(_retryDelays, streamable);
        Forget(_lastStreamed, streamable);
        Forget(_emptyStreams, streamable);

        var next = ChooseToStream(
            infoHashes,
            _streams.Keys.ToHashSet(StringComparer.Ordinal),
            _lastStreamed,
            MaxConcurrentStreams - _streams.Count);
        foreach (var infoHash in next)
        {
            _streams[infoHash] = Task.Run(() => StreamOneAsync(infoHash, stoppingToken), stoppingToken);
        }
    }

    /// <summary>
    /// Which tasks to subscribe to now: those not already streaming, least recently watched first — a
    /// task never watched goes before any that has been — up to <paramref name="free"/> of them.
    /// </summary>
    internal static IReadOnlyList<string> ChooseToStream(
        IEnumerable<string> streamable,
        IReadOnlySet<string> running,
        IReadOnlyDictionary<string, DateTimeOffset> lastStreamed,
        int free) =>
        free <= 0
            ? []
            : streamable
                .Distinct(StringComparer.Ordinal)
                .Where(infoHash => !running.Contains(infoHash))
                .OrderBy(infoHash => lastStreamed.TryGetValue(infoHash, out var at) ? at : DateTimeOffset.MinValue)
                .ThenBy(infoHash => infoHash, StringComparer.Ordinal)
                .Take(free)
                .ToList();

    /// <summary>Drops the entries of tasks no longer streamable, so a map does not grow for the life of the process.</summary>
    private static void Forget<T>(ConcurrentDictionary<string, T> map, HashSet<string> streamable)
    {
        foreach (var key in map.Keys)
        {
            if (!streamable.Contains(key))
            {
                map.TryRemove(key, out _);
            }
        }
    }

    private async Task StreamOneAsync(string infoHash, CancellationToken stoppingToken)
    {
        var produced = false;
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var engine = scope.ServiceProvider.GetRequiredService<ITorrentEngine>();
            await foreach (var snapshot in engine.StreamStatusAsync(infoHash, stoppingToken))
            {
                produced = true;
                await using var applyScope = scopeFactory.CreateAsyncScope();
                var service = applyScope.ServiceProvider.GetRequiredService<DownloadService>();
                await service.ApplyStatusAsync(infoHash, snapshot, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return; // shutting down
        }
        catch (Exception ex)
        {
            // Logged at the current backoff, so a sidecar that is gone says so a few times rather
            // than every two seconds for the length of the outage.
            // No identifier: an info-hash names what a household is transferring.
            logger.LogWarning(ex, "A download status stream ended early.");
        }

        _lastStreamed[infoHash] = DateTimeOffset.UtcNow;
        if (produced)
        {
            _emptyStreams.TryRemove(infoHash, out _);
        }
        else if (_emptyStreams.AddOrUpdate(infoHash, 1, (_, count) => count + 1) >= EmptyStreamsBeforeRecovery)
        {
            _emptyStreams.TryRemove(infoHash, out _);
            await RecoverIfLostAsync(infoHash, stoppingToken);
        }

        await BackOffAsync(infoHash, produced, stoppingToken);
    }

    /// <summary>
    /// Hands a transfer back to the engine when the engine no longer holds it. The decision, and the
    /// egress rule it answers to, belong to <see cref="DownloadService.RecoverLostAsync"/>; this only
    /// notices. Quiet on failure: an engine that cannot be asked is the stream's own warning already.
    /// </summary>
    private async Task RecoverIfLostAsync(string infoHash, CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<DownloadService>().RecoverLostAsync(infoHash, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not check whether the engine still holds a transfer.");
        }
    }

    /// <summary>
    /// Holds the subscription slot open for the backoff so discovery cannot re-subscribe underneath
    /// it. A stream that produced at least one snapshot is working and resets the delay to the floor.
    /// </summary>
    private async Task BackOffAsync(string infoHash, bool produced, CancellationToken stoppingToken)
    {
        if (produced)
        {
            _retryDelays.TryRemove(infoHash, out _);
            return;
        }

        var next = _retryDelays.TryGetValue(infoHash, out var current)
            ? TimeSpan.FromTicks(Math.Min(current.Ticks * 2, MaximumRetryDelay.Ticks))
            : MinimumRetryDelay;
        _retryDelays[infoHash] = next;

        try
        {
            await Task.Delay(next, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutting down
        }
    }
}
