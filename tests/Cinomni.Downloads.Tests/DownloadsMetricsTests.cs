using System.Diagnostics.Metrics;
using Cinomni.Downloads.Diagnostics;
using Cinomni.Kernel.Diagnostics;

namespace Cinomni.Downloads.Tests;

/// <summary>
/// Marks the download-gauge tests as the only ones running while they run. What is in flight is
/// process-wide state — an exporter subscribes to a meter name, not to a container — so another class
/// applying a snapshot in parallel would move the very numbers these assert on.
/// </summary>
[CollectionDefinition(DownloadsMetricsTests.Serial, DisableParallelization = true)]
public sealed class DownloadsMetricsCollection;

/// <summary>
/// "How many downloads are moving, and how fast" has to come back down. Finishing and removal already
/// brought it down; these pin the third way a torrent leaves — it simply stops reporting, after a
/// sidecar restart, an engine-side removal or a stream that never resumed — which no terminal state
/// ever announces and nothing else would ever notice.
/// </summary>
[Collection(Serial)]
public sealed class DownloadsMetricsTests
{
    internal const string Serial = "downloads-metrics";

    [Fact]
    public void A_torrent_that_stopped_reporting_is_neither_counted_nor_added_to_the_rate()
    {
        Clear();
        var now = DateTimeOffset.UtcNow;

        // Two downloads, one of which the engine stopped reporting long ago. Nothing called Forget for
        // it, because nothing knows: no terminal state was ever reached.
        DownloadsMetrics.RecordSnapshot(
            Hash(), 5_000, 500, peers: 4, isFinished: false, now - (DownloadsMetrics.SnapshotSilence * 2));

        var live = Hash();
        DownloadsMetrics.RecordSnapshot(live, 1_000, 100, peers: 4, isFinished: false, now);

        Assert.Equal(1, Read("cinomni.download.active"));

        // The rate gauge is the half that would otherwise keep adding a dead torrent's last speed for
        // the life of the process, which reads as a node doing work nobody asked for. Both directions
        // are summed here: 1000 down plus 100 up, and nothing from the torrent that left.
        Assert.Equal(1_100, Read("cinomni.download.rate"));

        DownloadsMetrics.Forget(live);
        Assert.Equal(0, Read("cinomni.download.active"));
    }

    [Fact]
    public void A_torrent_still_reporting_is_never_swept()
    {
        Clear();

        // Well inside the silence window, which is sixty of the pump's two-second snapshots: a retry
        // backoff or a slow tracker must never look like a torrent that left.
        var infoHash = Hash();
        DownloadsMetrics.RecordSnapshot(
            infoHash,
            10,
            1,
            peers: 1,
            isFinished: false,
            DateTimeOffset.UtcNow - (DownloadsMetrics.SnapshotSilence / 2));

        Assert.Equal(1, Read("cinomni.download.active"));
        Assert.Equal(11, Read("cinomni.download.rate"));

        DownloadsMetrics.Forget(infoHash);
        Assert.Equal(0, Read("cinomni.download.active"));
    }

    /// <summary>Info hashes are lookup keys here and never leave the process; synthetic ones are enough.</summary>
    private static string Hash() => Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// Empties what earlier tests in this process left in flight, by sweeping from far enough ahead that
    /// every entry has fallen silent. The instruments are process-wide, so a test that asserts an
    /// absolute number has to start from a known one.
    /// </summary>
    private static void Clear() => DownloadsMetrics.Sweep(DateTimeOffset.UtcNow + TimeSpan.FromDays(1));

    /// <summary>Sums the published measurements of one observable instrument, across every dimension.</summary>
    private static long Read(string instrumentName)
    {
        long total = 0;

        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == CinomniTelemetry.MeterName && instrument.Name == instrumentName)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };

        listener.SetMeasurementEventCallback<long>((_, measurement, _, _) => total += measurement);
        listener.Start();
        listener.RecordObservableInstruments();
        return total;
    }
}
