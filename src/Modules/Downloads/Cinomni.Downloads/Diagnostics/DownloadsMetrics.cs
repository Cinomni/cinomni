using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Collections.Concurrent;
using Cinomni.Kernel.Diagnostics;

namespace Cinomni.Downloads.Diagnostics;

/// <summary>
/// What a household actually asks about a download: is it moving, and if not, since when.
/// <para>
/// <b>No info hash, no torrent name, no file path — ever.</b> An info hash identifies exactly what is
/// being downloaded, which is the single most sensitive value this module handles; a torrent name says
/// the same thing in words. Both would also make the cardinality of these series unbounded. The
/// aggregate is the useful signal anyway: an operator wants "three downloads, none of them moving",
/// and the interface already names them for someone who is allowed to see them.
/// </para>
/// <para>
/// The tunnel guard is counted here too. An installation that opted into a VPN and then quietly ran
/// without one is the failure that matters most in this module, and a held download is otherwise only
/// visible to somebody reading the interface.
/// </para>
/// </summary>
public static class DownloadsMetrics
{
    private static readonly Meter Meter = new(CinomniTelemetry.MeterName);

    private static readonly KeyValuePair<string, object?> ModuleTag =
        new(CinomniTelemetry.Tags.Module, CinomniTelemetry.Modules.Downloads);

    /// <summary>
    /// The live rate per in-flight torrent, keyed by info hash so a second snapshot for the same
    /// torrent replaces rather than adds. <b>The key never leaves this dictionary</b> — it is a lookup
    /// handle, not a label, and only the sum is ever published.
    /// </summary>
    private static readonly ConcurrentDictionary<string, TransferRates> InFlight = new(StringComparer.Ordinal);

    /// <summary>
    /// How long a torrent may go without reporting before it stops counting as in flight. Sixty missed
    /// snapshots at the pump's two-second cadence, so an ordinary hiccup or a retry backoff never drops
    /// a live download.
    /// <para>
    /// This is what closes the entry points a terminal state does not cover: a sidecar restart, an
    /// engine-side removal, a task that simply stops producing snapshots. Without it the active gauge
    /// never comes back down and the rate gauge keeps adding a dead torrent's last speed for the life of
    /// the process — which is exactly what these two instruments say they do not do.
    /// </para>
    /// </summary>
    internal static readonly TimeSpan SnapshotSilence = TimeSpan.FromMinutes(2);

    private static readonly Counter<long> Stalled = Meter.CreateCounter<long>(
        "cinomni.download.stalled",
        unit: "{observation}",
        description: "Snapshots reporting an unfinished download with no peers and no transfer.");

    private static readonly Counter<long> Finished = Meter.CreateCounter<long>(
        "cinomni.download.finished",
        unit: "{download}",
        description: "Downloads that reached a terminal state, by outcome.");

    private static readonly Counter<long> TunnelHolds = Meter.CreateCounter<long>(
        "cinomni.download.tunnel_holds",
        unit: "{event}",
        description: "Times the egress guard held or released downloads because the tunnel changed state.");

    static DownloadsMetrics()
    {
        Meter.CreateObservableGauge(
            "cinomni.download.active",
            () => new Measurement<long>(Sweep(DateTimeOffset.UtcNow), ModuleTag),
            unit: "{download}",
            description: "Downloads currently reporting progress.");

        Meter.CreateObservableGauge(
            "cinomni.download.rate",
            ObserveRates,
            unit: "By/s",
            description: "Aggregate transfer rate over all in-flight downloads, by direction.");
    }

    /// <summary>
    /// Records one status snapshot.
    /// </summary>
    /// <param name="infoHash">Lookup key only. Never published, never tagged, never exported.</param>
    /// <param name="downloadRateBytes">Bytes per second down, as the engine reported them.</param>
    /// <param name="uploadRateBytes">Bytes per second up.</param>
    /// <param name="peers">Connected peers plus seeds.</param>
    /// <param name="isFinished">Whether the torrent has all its data.</param>
    /// <param name="now">When the engine reported it, which is what makes silence detectable.</param>
    public static void RecordSnapshot(
        string infoHash,
        long downloadRateBytes,
        long uploadRateBytes,
        int peers,
        bool isFinished,
        DateTimeOffset now)
    {
        if (isFinished)
        {
            InFlight.TryRemove(infoHash, out _);
            return;
        }

        InFlight[infoHash] = new TransferRates(downloadRateBytes, uploadRateBytes, now);

        // A stall is the failure a household notices and cannot explain: the download is "running" and
        // nothing is happening. Counted per observation rather than as a gauge, so the rate of stalling
        // is visible even when a torrent recovers between two scrapes.
        if (peers == 0 && downloadRateBytes == 0)
        {
            Stalled.Add(1, ModuleTag);
        }
    }

    /// <summary>Records a download reaching a terminal state and stops counting it as in flight.</summary>
    /// <param name="outcome">A bounded word: <c>completed</c> or <c>failed</c>.</param>
    public static void RecordFinished(string infoHash, string outcome)
    {
        Forget(infoHash);
        Finished.Add(
            1,
            ModuleTag,
            new KeyValuePair<string, object?>(CinomniTelemetry.Tags.Outcome, outcome));
    }

    /// <summary>
    /// Stops counting a torrent that left the engine without finishing — a removal.
    /// <para>
    /// It counts nothing: a removal is neither a completion nor a failure, and inventing an outcome for
    /// it would put operator housekeeping into the series that says whether downloads succeed. What it
    /// must do is drop the entry, because no further snapshot will ever arrive for it: without this the
    /// active gauge never comes back down and the rate gauge keeps adding the last speed a torrent was
    /// seen at, for the life of the process.
    /// </para>
    /// </summary>
    /// <param name="infoHash">Lookup key only. Never published, never tagged, never exported.</param>
    public static void Forget(string infoHash) => InFlight.TryRemove(infoHash, out _);

    /// <summary>
    /// Records the egress guard acting. <paramref name="state"/> is <c>held</c> or <c>released</c>, and
    /// the tunnel device name is deliberately not a tag: it is local network configuration.
    /// </summary>
    public static void RecordTunnelTransition(string state) =>
        TunnelHolds.Add(
            1,
            ModuleTag,
            new KeyValuePair<string, object?>(CinomniTelemetry.Tags.State, state));

    /// <summary>
    /// Drops every torrent that has gone silent past <see cref="SnapshotSilence"/> and returns how many
    /// are left. Runs on the gauge callbacks, which are the only readers: an entry nobody is reading
    /// costs nothing, and eviction has to happen even when no snapshot ever arrives again — that is the
    /// case it exists for.
    /// </summary>
    internal static int Sweep(DateTimeOffset now)
    {
        foreach (var entry in InFlight)
        {
            if (now - entry.Value.SeenAt > SnapshotSilence)
            {
                // Removing while enumerating is defined behaviour on this dictionary, and a snapshot
                // arriving in between simply re-adds the entry with a fresh timestamp.
                InFlight.TryRemove(entry.Key, out _);
            }
        }

        return InFlight.Count;
    }

    private static IEnumerable<Measurement<long>> ObserveRates()
    {
        var now = DateTimeOffset.UtcNow;
        long down = 0;
        long up = 0;

        Sweep(now);

        foreach (var rates in InFlight.Values)
        {
            // Re-checked per entry: a torrent can fall silent between the sweep and this loop, and a
            // dead torrent's last speed must never be added to a live total.
            if (now - rates.SeenAt > SnapshotSilence)
            {
                continue;
            }

            down += rates.Down;
            up += rates.Up;
        }

        return
        [
            new Measurement<long>(down, ModuleTag, new KeyValuePair<string, object?>(CinomniTelemetry.Tags.Direction, "down")),
            new Measurement<long>(up, ModuleTag, new KeyValuePair<string, object?>(CinomniTelemetry.Tags.Direction, "up")),
        ];
    }

    private readonly record struct TransferRates(long Down, long Up, DateTimeOffset SeenAt);
}
