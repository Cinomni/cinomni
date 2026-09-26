using System.Diagnostics.Metrics;
using Cinomni.Catalog.Contracts;
using Cinomni.Kernel.Diagnostics;
using Cinomni.Kernel.Identifiers;
using Cinomni.Kernel.Messaging;
using Cinomni.Kernel.Security;
using Cinomni.Library.Contracts;
using Cinomni.Playback.Contracts;
using Cinomni.Playback.Diagnostics;
using Cinomni.Playback.Messaging;
using Cinomni.Playback.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Playback.Tests;

/// <summary>
/// Marks the transcode-gauge tests as the only ones running while they run. Concurrency on this node is
/// process-wide state — an exporter subscribes to a meter name, not to a container — so another class
/// starting a session in parallel would move the very number these assert on.
/// </summary>
[CollectionDefinition(TranscodeGaugeTests.Serial, DisableParallelization = true)]
public sealed class TranscodeGaugeCollection;

/// <summary>
/// "How many transcodes are running" is the number that decides whether a household is watching or
/// buffering, so it has to mean what it says. These pin the two ways it goes wrong: counting a session
/// that never started a process, and never uncounting one that ended.
/// </summary>
[Collection(Serial)]
public sealed class TranscodeGaugeTests : IAsyncLifetime
{
    internal const string Serial = "playback-transcode-gauge";

    private const string MoviePath = "/data/library/Gauge.2024/Gauge.2024.mkv";

    private readonly FakeMediaEncoder _encoder = new();
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync() =>
        _provider = await PlaybackTestHost.CreateAsync("cinomni_test_playback_gauge", _encoder);

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public void Only_a_session_that_encodes_counts_against_this_node()
    {
        // Direct play is a file being read: no FFmpeg process, nothing to count. Remux and transcode
        // both put one on the node — a remux is cheaper, not free.
        Assert.False(PlaybackMetrics.IsEncoded(PlaybackMethod.DirectPlay));
        Assert.True(PlaybackMetrics.IsEncoded(PlaybackMethod.Remux));
        Assert.True(PlaybackMetrics.IsEncoded(PlaybackMethod.Transcode));
    }

    [Fact]
    public async Task Stopping_a_direct_play_does_not_take_somebody_elses_transcode_off_the_gauge()
    {
        var assetId = await RegisterAssetAsync();
        var watcher = Uuid7.New();

        // A client that can play the file as it is. This session costs the node nothing.
        var ticket = await RequestAsync(watcher, assetId, Client(["matroska"], ["h264"], ["aac"]));
        Assert.Equal(PlaybackMethod.DirectPlay, ticket.Method);

        // Something else really is encoding on this node while that viewer watches.
        var somebodyElse = Guid.NewGuid();
        PlaybackMetrics.RecordStarted(somebodyElse, PlaybackMethod.Transcode.ToString());
        var busy = Read();

        await StopAsync(watcher, ticket.SessionId);

        // The transcode is still running. A gauge that under-reports here is worse than no gauge: the
        // node looks idle at exactly the moment it is at its most loaded.
        Assert.Equal(busy, Read());

        PlaybackMetrics.RecordStopped(somebodyElse);
        Assert.Equal(busy - 1, Read());
    }

    /// <summary>
    /// The path nothing else covers: a viewer whose browser died never sends a stop and never crosses
    /// the watched threshold, so the only server-side event that ever recognises the session is the
    /// retention sweep. Without it the gauge keeps the +1 for the life of the process, and on a
    /// household box that restarts rarely the number an operator judges the node by only ever climbs.
    /// </summary>
    [Fact]
    public async Task A_session_nobody_ever_ended_comes_off_the_gauge_when_retention_deletes_it()
    {
        var assetId = await RegisterAssetAsync();
        var watcher = Uuid7.New();

        var idle = Read();

        var ticket = await RequestAsync(watcher, assetId, Client(["matroska"], ["vp9"], ["aac"]));
        Assert.Equal(PlaybackMethod.Transcode, ticket.Method);
        Assert.Equal(idle + 1, Read());

        // The tab closed a long time ago and no stop ever arrived.
        await AgeAsync(ticket.SessionId.Value, DateTimeOffset.UtcNow - TimeSpan.FromDays(400));
        await PurgeAsync();

        Assert.Equal(idle, Read());

        // And a second sweep — or one in a process that never counted the session — must not subtract
        // anything else. That is why the gauge tracks identity rather than a bare count.
        var somebodyElse = Guid.NewGuid();
        PlaybackMetrics.RecordStarted(somebodyElse, PlaybackMethod.Transcode.ToString());
        var busy = Read();

        await PurgeAsync();
        PlaybackMetrics.RecordStopped(ticket.SessionId.Value);
        Assert.Equal(busy, Read());

        PlaybackMetrics.RecordStopped(somebodyElse);
        Assert.Equal(idle, Read());
    }

    [Fact]
    public async Task A_transcode_is_counted_while_it_runs_and_uncounted_when_the_session_stops()
    {
        var assetId = await RegisterAssetAsync();
        var watcher = Uuid7.New();

        var idle = Read();

        // A client that cannot play this video: the planner asks for a transcode and the encoder starts.
        var ticket = await RequestAsync(watcher, assetId, Client(["matroska"], ["vp9"], ["aac"]));
        Assert.Equal(PlaybackMethod.Transcode, ticket.Method);
        Assert.Equal(idle + 1, Read());

        await StopAsync(watcher, ticket.SessionId);
        Assert.Equal(idle, Read());

        // A redelivered stop is a no-op on the session, and must be one on the gauge too — otherwise a
        // retried command quietly subtracts a transcode that is still running.
        await StopAsync(watcher, ticket.SessionId);
        Assert.Equal(idle, Read());
    }

    /// <summary>
    /// The watched threshold completes the session at 90 %, but FFmpeg is still converting the credits:
    /// the node is exactly as busy as before, so the gauge must not drop until the stream really ends.
    /// </summary>
    [Fact]
    public async Task Crossing_the_watched_threshold_keeps_the_stream_on_the_gauge_until_it_ends()
    {
        var assetId = await RegisterAssetAsync();
        var watcher = Uuid7.New();
        var idle = Read();

        var ticket = await RequestAsync(watcher, assetId, Client(["matroska"], ["vp9"], ["aac"]));
        Assert.Equal(idle + 1, Read());

        await using (var scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IPlaybackSessionCommands>().ReportProgressAsync(
                new Viewer(watcher, IsAdministrator: false), ticket.SessionId, positionTicks: 950, durationTicks: 1000, isPaused: false);
        }

        Assert.Equal(idle + 1, Read());

        await StopAsync(watcher, ticket.SessionId);
        Assert.Equal(idle, Read());
    }

    /// <summary>Reads the published concurrency gauge.</summary>
    private static long Read()
    {
        long active = 0;

        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == CinomniTelemetry.MeterName
                    && instrument.Name == "cinomni.transcode.active")
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };

        listener.SetMeasurementEventCallback<long>((_, measurement, _, _) => active = measurement);
        listener.Start();
        listener.RecordObservableInstruments();
        return active;
    }

    private static ClientCapability Client(string[] containers, string[] video, string[] audio) =>
        new(containers, video, audio, MaxHeight: null);

    private async Task<Guid> RegisterAssetAsync()
    {
        Guid workId;
        await using (var scope = _provider.CreateAsyncScope())
        {
            var added = await scope.ServiceProvider.GetRequiredService<ICatalogCommands>()
                .AddMovieAsync("Gauge", 2024, []);
            workId = added.Value.Value;
        }

        var assetId = Uuid7.New();
        var request = new RegisterMediaAssetRequest(
            assetId,
            WorkId: workId,
            TargetIds: [Uuid7.New()],
            FullPath: MoviePath,
            Size: 2_000_000_000,
            Container: "matroska",
            Streams:
            [
                new MediaStreamInput(0, MediaStreamType.Video, "h264", null, null, 1920, 1080, null, null, true, false),
                new MediaStreamInput(1, MediaStreamType.Audio, "aac", "eng", 6, null, null, null, null, true, false),
            ]);

        await using var assetScope = _provider.CreateAsyncScope();
        await assetScope.ServiceProvider.GetRequiredService<ILibraryCommands>().RegisterMediaAssetAsync(request);
        return assetId;
    }

    private async Task<PlaybackTicket> RequestAsync(Guid userId, Guid assetId, ClientCapability capability)
    {
        await using var scope = _provider.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<IPlaybackSessionCommands>()
            .RequestPlaybackAsync(new Viewer(userId, IsAdministrator: false), assetId, capability);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
        return result.Value;
    }

    private async Task StopAsync(Guid userId, PlaybackSessionId sessionId)
    {
        await using var scope = _provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IPlaybackSessionCommands>()
            .StopPlaybackAsync(userId, sessionId);
    }

    /// <summary>Backdates a session's last activity, which is how a client that vanished looks.</summary>
    private async Task AgeAsync(Guid sessionId, DateTimeOffset updatedAt)
    {
        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PlaybackDbContext>();
        await dbContext.Sessions
            .Where(s => s.Id == sessionId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.UpdatedAt, updatedAt));
    }

    private async Task PurgeAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<PurgePlaybackSessionsCommand>>();
        Assert.True((await handler.HandleAsync(new PurgePlaybackSessionsCommand())).IsSuccess);
    }
}
