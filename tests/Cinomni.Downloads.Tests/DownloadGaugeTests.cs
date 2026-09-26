using System.Diagnostics.Metrics;
using Cinomni.Downloads.Application;
using Cinomni.Downloads.Contracts;
using Cinomni.Downloads.Engine;
using Cinomni.Downloads.Persistence;
using Cinomni.Kernel.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Downloads.Tests;

/// <summary>
/// Marks the gauge tests as the only ones running while they run. The in-flight view is process-wide
/// state — an exporter subscribes to a meter name, not to a container — so another class advancing a
/// download in parallel would move the very numbers these assert on.
/// </summary>
[CollectionDefinition(DownloadGaugeTests.Serial, DisableParallelization = true)]
public sealed class DownloadGaugeCollection;

/// <summary>
/// A gauge that only ever goes up is worse than no gauge: an operator reads "four downloads running"
/// off a node that is doing nothing and stops trusting the number. These pin the two ways a torrent
/// leaves the in-flight view — it finishes, or an operator removes it — and pin that neither the info
/// hash nor the torrent name goes anywhere near a measurement.
/// </summary>
[Collection(Serial)]
public sealed class DownloadGaugeTests : IAsyncLifetime
{
    internal const string Serial = "downloads-gauges";

    /// <summary>A rate no other test uses, so a stray measurement would be obvious rather than plausible.</summary>
    private const long DistinctiveRate = 987_654L;

    private readonly FakeTorrentEngine _engine = new();
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync()
    {
        // An info hash of this class's own. The in-flight view is keyed by it and is process-wide, so
        // reusing the shared fixture hash would land on an entry another class left behind and the
        // count would not move at all.
        _engine.NextInfoHash = $"gauge-{Guid.NewGuid():N}";
        _provider = await DownloadsTestHost.CreateAsync("cinomni_test_downloads_gauges", _engine);
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task A_removed_download_stops_being_counted_and_stops_adding_to_the_rate()
    {
        var attemptId = Guid.NewGuid();
        await AddAsync(attemptId);
        var infoHash = _engine.NextInfoHash;

        var idle = Read();

        // One live snapshot: the torrent is in flight and moving.
        await ApplyStatusAsync(Downloading(infoHash));

        var running = Read();
        Assert.Equal(idle.Active + 1, running.Active);
        Assert.Equal(idle.DownRate + DistinctiveRate, running.DownRate);

        // An operator removes it. No further snapshot will ever arrive for this torrent, so unless the
        // removal says so the count stays up and its last rate keeps being added for the life of the
        // process — the failure this test exists for.
        var removed = await RemoveAsync(attemptId);
        Assert.True(removed);

        var after = Read();
        Assert.Equal(idle.Active, after.Active);
        Assert.Equal(idle.DownRate, after.DownRate);
    }

    [Fact]
    public async Task A_finished_download_stops_being_counted_too()
    {
        var attemptId = Guid.NewGuid();
        await AddAsync(attemptId);
        var infoHash = _engine.NextInfoHash;

        var idle = Read();
        await ApplyStatusAsync(Downloading(infoHash));
        Assert.Equal(idle.Active + 1, Read().Active);

        // The ordinary ending: the engine reports it has all the data.
        await ApplyStatusAsync(Downloading(infoHash) with { IsFinished = true, Progress = 1.0 });

        var after = Read();
        Assert.Equal(idle.Active, after.Active);
        Assert.Equal(idle.DownRate, after.DownRate);
    }

    [Fact]
    public async Task No_measurement_carries_the_info_hash_or_the_torrent_name()
    {
        var attemptId = Guid.NewGuid();
        await AddAsync(attemptId);
        var infoHash = _engine.NextInfoHash;

        var recorder = new TagRecorder();
        using var listener = recorder.Listen();

        await ApplyStatusAsync(Downloading(infoHash));
        await ApplyStatusAsync(Downloading(infoHash) with { IsFinished = true, Progress = 1.0 });
        listener.RecordObservableInstruments();

        // An info hash identifies exactly what is being downloaded and a name says it in words. Both
        // would also make these series unbounded. Only a module, a direction and an outcome are allowed.
        Assert.NotEmpty(recorder.Tags);
        foreach (var tags in recorder.Tags)
        {
            Assert.All(tags, tag =>
            {
                Assert.StartsWith("cinomni.", tag.Key, StringComparison.Ordinal);
                var text = tag.Value as string ?? string.Empty;
                Assert.DoesNotContain(infoHash, text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(_engine.NextName, text, StringComparison.OrdinalIgnoreCase);
            });
        }
    }

    /// <summary>A snapshot that is unambiguously in flight and moving at a recognisable speed.</summary>
    private static TorrentSnapshot Downloading(string infoHash) =>
        FakeTorrentEngine.Snapshot(infoHash, "downloading", progress: 0.5) with
        {
            DownloadRate = DistinctiveRate,
            NumPeers = 4,
        };

    private async Task AddAsync(Guid attemptId)
    {
        await using var scope = _provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DownloadService>()
            .AddDownloadAsync(attemptId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "g1", "magnet:g1");
    }

    private async Task ApplyStatusAsync(TorrentSnapshot snapshot)
    {
        await using var scope = _provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DownloadService>()
            .ApplyStatusAsync(snapshot.InfoHash, snapshot);
    }

    private async Task<bool> RemoveAsync(Guid attemptId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DownloadsDbContext>();
        var id = await dbContext.Tasks
            .AsNoTracking()
            .Where(task => task.AttemptId == attemptId)
            .Select(task => task.Id)
            .FirstAsync();

        return await scope.ServiceProvider.GetRequiredService<DownloadService>()
            .RemoveAsync(new DownloadTaskId(id), deleteFiles: false);
    }

    /// <summary>Reads the two published gauges by asking the meter for its observable values.</summary>
    private static (long Active, long DownRate) Read()
    {
        long active = 0;
        long down = 0;

        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == CinomniTelemetry.MeterName
                    && instrument.Name is "cinomni.download.active" or "cinomni.download.rate")
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };

        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            if (instrument.Name == "cinomni.download.active")
            {
                active = measurement;
                return;
            }

            foreach (var tag in tags)
            {
                if (tag.Key == CinomniTelemetry.Tags.Direction && (tag.Value as string) == "down")
                {
                    down = measurement;
                }
            }
        });

        listener.Start();
        listener.RecordObservableInstruments();
        return (active, down);
    }

    /// <summary>Captures the tags of every downloads measurement, whichever instrument it came from.</summary>
    private sealed class TagRecorder
    {
        public List<KeyValuePair<string, object?>[]> Tags { get; } = [];

        public MeterListener Listen()
        {
            var listener = new MeterListener
            {
                InstrumentPublished = (instrument, meterListener) =>
                {
                    if (instrument.Meter.Name == CinomniTelemetry.MeterName
                        && instrument.Name.StartsWith("cinomni.download.", StringComparison.Ordinal))
                    {
                        meterListener.EnableMeasurementEvents(instrument);
                    }
                },
            };

            listener.SetMeasurementEventCallback<long>((_, _, tags, _) => Tags.Add(tags.ToArray()));
            listener.SetMeasurementEventCallback<double>((_, _, tags, _) => Tags.Add(tags.ToArray()));
            listener.Start();
            return listener;
        }
    }
}
