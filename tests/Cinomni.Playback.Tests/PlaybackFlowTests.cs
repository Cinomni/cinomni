using System.Collections.Concurrent;
using Cinomni.Catalog.Contracts;
using Cinomni.Kernel.Identifiers;
using Cinomni.Kernel.Security;
using Cinomni.Kernel.Messaging;
using Cinomni.Library.Contracts;
using Cinomni.Operations.Messaging;
using Cinomni.Playback.Contracts;
using Cinomni.Playback.Encoding;
using Cinomni.Playback.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Playback.Tests;

/// <summary>
/// Integration tests for the playback spine against a real PostgreSQL instance: a registered library
/// asset is planned (DirectPlay/Remux/Transcode, explained), a session opens, progress is tracked and
/// resumes, and the (faked) encoder is driven for transcodes. Events are captured through outbox sinks.
/// </summary>
public sealed class PlaybackFlowTests : IAsyncLifetime
{
    private const string MoviePath = "/data/library/Movie.2024/Movie.2024.mkv";

    private readonly FakeMediaEncoder _encoder = new();
    private readonly EventSink _sink = new();
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync() =>
        _provider = await PlaybackTestHost.CreateAsync("cinomni_test_playback", _encoder, services =>
        {
            services.AddSingleton(_sink);
            services.AddScoped<IEventHandler<PlaybackStarted>, StartedSink>();
            services.AddScoped<IEventHandler<PlaybackCompleted>, CompletedSink>();
            services.AddScoped<IEventHandler<TranscodeStarted>, TranscodeStartedSink>();
        });

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task A_session_is_only_readable_by_the_user_who_owns_it()
    {
        var assetId = await RegisterAssetAsync();
        var owner = Uuid7.New();
        var ticket = await RequestAsync(owner, assetId, Client(["matroska"], ["h264"], ["aac"]));

        // Installations are multi-account: knowing a session id is not permission to read it.
        Assert.NotNull(await QueryAsync(q => q.GetSessionAsync(owner, ticket.SessionId)));
        Assert.Null(await QueryAsync(q => q.GetSessionAsync(Uuid7.New(), ticket.SessionId)));
    }

    /// <summary>
    /// An upgrade usually lands on the very path of the file it replaced. The old asset's streams then
    /// describe a file that is no longer there, so playing it would serve the new bytes under the old
    /// description: only an active asset plays.
    /// </summary>
    [Fact]
    public async Task An_asset_that_was_upgraded_away_is_not_played()
    {
        var workId = await AddWorkAsync();
        var old = await RegisterAsync(workId);
        var current = await RegisterAsync(workId);

        await using var scope = _provider.CreateAsyncScope();
        var commands = scope.ServiceProvider.GetRequiredService<IPlaybackSessionCommands>();
        var refused = await commands.RequestPlaybackAsync(Watcher(Uuid7.New()), old, Client(["matroska"], ["h264"], ["aac"]));
        var played = await commands.RequestPlaybackAsync(Watcher(Uuid7.New()), current, Client(["matroska"], ["h264"], ["aac"]));

        Assert.Equal("playback.asset_not_found", refused.Error.Code);
        Assert.True(played.IsSuccess);

        async Task<Guid> RegisterAsync(Guid work)
        {
            var assetId = Uuid7.New();
            await using var registration = _provider.CreateAsyncScope();
            await registration.ServiceProvider.GetRequiredService<ILibraryCommands>().RegisterMediaAssetAsync(
                new RegisterMediaAssetRequest(
                    assetId, WorkId: work, TargetIds: [Uuid7.New()], FullPath: MoviePath, Size: 2_000_000_000,
                    Container: "matroska",
                    Streams:
                    [
                        new MediaStreamInput(0, MediaStreamType.Video, "h264", null, null, 1920, 1080, null, null, true, false),
                        new MediaStreamInput(1, MediaStreamType.Audio, "aac", "eng", 6, null, null, null, null, true, false),
                    ],
                    UnitIds: [work]));
            return assetId;
        }
    }

    [Fact]
    public async Task A_compatible_client_gets_direct_play()
    {
        var assetId = await RegisterAssetAsync();
        var userId = Uuid7.New();

        var ticket = await RequestAsync(userId, assetId, Client(["matroska"], ["h264"], ["aac"]));

        Assert.Equal(PlaybackMethod.DirectPlay, ticket.Method);
        Assert.Equal(PlaybackState.DirectPlaying, await SessionStateAsync(userId, ticket.SessionId));
        Assert.Empty(_encoder.Requests);
        await DrainOutboxAsync();
        Assert.Contains(ticket.SessionId.Value, _sink.Started);
    }

    [Fact]
    public async Task An_incompatible_codec_forces_a_transcode()
    {
        var assetId = await RegisterAssetAsync();
        var userId = Uuid7.New();

        var ticket = await RequestAsync(userId, assetId, Client(["matroska"], ["vp9"], ["aac"]));

        Assert.Equal(PlaybackMethod.Transcode, ticket.Method);
        Assert.Equal(PlaybackState.Transcoding, await SessionStateAsync(userId, ticket.SessionId));
        Assert.Single(_encoder.Requests);
        Assert.Equal("libx264", _encoder.Requests[0].VideoCodec);
        Assert.Equal(EncoderBackend.Software, _encoder.Requests[0].Backend);
        Assert.Contains(ticket.Plan.TranscodeReasons, r => r.Contains("h264"));
        await DrainOutboxAsync();
        Assert.Contains(ticket.SessionId.Value, _sink.TranscodeStarted);
    }

    [Fact]
    public async Task A_detected_hardware_backend_reaches_the_encoder_request()
    {
        // The startup probe publishes here; a request planned afterwards must carry it through to
        // the FFmpeg request, not just the explanation on the ticket.
        await using (var scope = _provider.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<HardwareCapabilitiesCache>()
                .Publish(new HardwareCapabilities([EncoderBackend.Vaapi]));
        }

        var assetId = await RegisterAssetAsync();
        var userId = Uuid7.New();

        var ticket = await RequestAsync(userId, assetId, Client(["matroska"], ["vp9"], ["aac"]));

        Assert.Equal(EncoderBackend.Vaapi, ticket.Plan.Backend);
        Assert.Single(_encoder.Requests);
        Assert.Equal(EncoderBackend.Vaapi, _encoder.Requests[0].Backend);
        // Encode capability alone says nothing about decode: this probe result named no decoder, so
        // the source is decoded in software even though the encode is accelerated.
        Assert.False(_encoder.Requests[0].AccelerateDecode);
    }

    [Fact]
    public async Task A_hardware_decoder_for_the_sources_own_codec_reaches_the_encoder_request()
    {
        // The registered asset is h264, and this probe result names a VAAPI decoder for exactly that
        // codec — so the full hardware pipeline is what the encoder must be asked for.
        await using (var scope = _provider.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<HardwareCapabilitiesCache>().Publish(
                new HardwareCapabilities([EncoderBackend.Vaapi], [new DecodeCapability(EncoderBackend.Vaapi, "h264")]));
        }

        var assetId = await RegisterAssetAsync();
        var userId = Uuid7.New();

        var ticket = await RequestAsync(userId, assetId, Client(["matroska"], ["vp9"], ["aac"]));

        Assert.True(ticket.Plan.DecodeAccelerated);
        Assert.Single(_encoder.Requests);
        Assert.True(_encoder.Requests[0].AccelerateDecode);
    }

    [Fact]
    public async Task A_source_codec_with_no_hardware_decoder_still_gets_an_accelerated_encode()
    {
        // The asymmetry this whole flag exists for: the backend decodes hevc on this host, but the
        // asset is h264. The encode is still accelerated; only the decode stays in software.
        await using (var scope = _provider.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<HardwareCapabilitiesCache>().Publish(
                new HardwareCapabilities([EncoderBackend.Vaapi], [new DecodeCapability(EncoderBackend.Vaapi, "hevc")]));
        }

        var assetId = await RegisterAssetAsync();
        var userId = Uuid7.New();

        var ticket = await RequestAsync(userId, assetId, Client(["matroska"], ["vp9"], ["aac"]));

        Assert.Equal(EncoderBackend.Vaapi, ticket.Plan.Backend);
        Assert.False(ticket.Plan.DecodeAccelerated);
        Assert.False(Assert.Single(_encoder.Requests).AccelerateDecode);
    }

    [Fact]
    public async Task A_hardware_fallback_is_persisted_on_the_transcode_job()
    {
        // The encoder itself performs the bounded retry; PlaybackService only has to notice the
        // mismatch between what was planned and what actually ran, and persist why.
        await using (var scope = _provider.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<HardwareCapabilitiesCache>()
                .Publish(new HardwareCapabilities([EncoderBackend.Vaapi]));
        }

        _encoder.FallenBackTo = EncoderBackend.Software;

        var assetId = await RegisterAssetAsync();
        var userId = Uuid7.New();
        var ticket = await RequestAsync(userId, assetId, Client(["matroska"], ["vp9"], ["aac"]));

        // The plan still explains that hardware was chosen — the job is where the runtime discrepancy lives.
        Assert.Equal(EncoderBackend.Vaapi, ticket.Plan.Backend);

        await using var scope2 = _provider.CreateAsyncScope();
        var dbContext = scope2.ServiceProvider.GetRequiredService<PlaybackDbContext>();
        var job = Assert.Single(dbContext.TranscodeJobs, j => j.SessionId == ticket.SessionId.Value);
        Assert.True(job.FellBackToSoftware);
        Assert.Contains("Vaapi", job.FallbackReason);
        Assert.Contains("Software", job.FallbackReason);
    }

    [Fact]
    public async Task An_unsupported_container_forces_a_remux()
    {
        var assetId = await RegisterAssetAsync();
        var userId = Uuid7.New();

        var ticket = await RequestAsync(userId, assetId, Client(["mp4"], ["h264"], ["aac"]));

        Assert.Equal(PlaybackMethod.Remux, ticket.Method);
        Assert.Single(_encoder.Requests);
        Assert.Equal("copy", _encoder.Requests[0].VideoCodec);
    }

    [Fact]
    public async Task Progress_is_tracked_and_resumed()
    {
        var assetId = await RegisterAssetAsync();
        var userId = Uuid7.New();
        var ticket = await RequestAsync(userId, assetId, Client(["matroska"], ["h264"], ["aac"]));

        await ReportAsync(userId, ticket.SessionId, positionTicks: 400, isPaused: false);

        var progress = await QueryAsync(q => q.GetProgressAsync(userId, assetId));
        Assert.Equal(400, progress!.PositionTicks);

        // A fresh request resumes from the saved position.
        var resumed = await RequestAsync(userId, assetId, Client(["matroska"], ["h264"], ["aac"]));
        Assert.Equal(400, resumed.ResumePositionTicks);
    }

    [Fact]
    public async Task Watching_past_the_threshold_completes_the_session()
    {
        var assetId = await RegisterAssetAsync();
        var userId = Uuid7.New();
        var ticket = await RequestAsync(userId, assetId, Client(["matroska"], ["h264"], ["aac"]));

        await ReportAsync(userId, ticket.SessionId, positionTicks: 960, isPaused: false);

        Assert.Equal(PlaybackState.Completed, await SessionStateAsync(userId, ticket.SessionId));
        var progress = await QueryAsync(q => q.GetProgressAsync(userId, assetId));
        Assert.True(progress!.Played);
        await DrainOutboxAsync();
        Assert.Contains(ticket.SessionId.Value, _sink.Completed);
    }

    [Fact]
    public async Task Stopping_completes_the_session()
    {
        var assetId = await RegisterAssetAsync();
        var userId = Uuid7.New();
        var ticket = await RequestAsync(userId, assetId, Client(["matroska"], ["h264"], ["aac"]));

        await using (var scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IPlaybackSessionCommands>()
                .StopPlaybackAsync(userId, ticket.SessionId);
        }

        Assert.Equal(PlaybackState.Completed, await SessionStateAsync(userId, ticket.SessionId));
    }

    [Fact]
    public async Task Requesting_an_unknown_asset_fails()
    {
        var userId = Uuid7.New();

        await using var scope = _provider.CreateAsyncScope();
        var commands = scope.ServiceProvider.GetRequiredService<IPlaybackSessionCommands>();
        var result = await commands.RequestPlaybackAsync(Watcher(userId), Uuid7.New(), Client(["matroska"], ["h264"], ["aac"]));

        Assert.True(result.IsFailure);
    }

    // -- helpers ---------------------------------------------------------------------------------

    private static ClientCapability Client(string[] containers, string[] video, string[] audio) =>
        new(containers, video, audio, MaxHeight: null);

    /// <summary>A real catalogued work in the default (open) collection — playing implies being allowed to see.</summary>
    private async Task<Guid> AddWorkAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var added = await scope.ServiceProvider.GetRequiredService<ICatalogCommands>()
            .AddMovieAsync("The Matrix", 1999, []);
        return added.Value.Value;
    }

    private async Task<Guid> RegisterAssetAsync()
    {
        var assetId = Uuid7.New();
        var request = new RegisterMediaAssetRequest(
            assetId,
            WorkId: await AddWorkAsync(),
            TargetIds: [Uuid7.New()],
            FullPath: MoviePath,
            Size: 2_000_000_000,
            Container: "matroska",
            Streams:
            [
                new MediaStreamInput(0, MediaStreamType.Video, "h264", null, null, 1920, 1080, null, null, true, false),
                new MediaStreamInput(1, MediaStreamType.Audio, "aac", "eng", 6, null, null, null, null, true, false),
            ]);

        await using var scope = _provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ILibraryCommands>().RegisterMediaAssetAsync(request);
        return assetId;
    }

    /// <summary>A plain member: they see the default collection, which is open.</summary>
    private static Viewer Watcher(Guid userId) => new(userId, IsAdministrator: false);

    private async Task<PlaybackTicket> RequestAsync(Guid userId, Guid assetId, ClientCapability capability)
    {
        await using var scope = _provider.CreateAsyncScope();
        var commands = scope.ServiceProvider.GetRequiredService<IPlaybackSessionCommands>();
        var result = await commands.RequestPlaybackAsync(Watcher(userId), assetId, capability);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
        return result.Value;
    }

    private async Task ReportAsync(Guid userId, PlaybackSessionId sessionId, long positionTicks, bool isPaused)
    {
        await using var scope = _provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IPlaybackSessionCommands>()
            .ReportProgressAsync(Watcher(userId), sessionId, positionTicks, durationTicks: 1000, isPaused);
    }

    private async Task<PlaybackState> SessionStateAsync(Guid userId, PlaybackSessionId sessionId)
    {
        var detail = await QueryAsync(q => q.GetSessionAsync(userId, sessionId));
        return detail!.Session.State;
    }

    private async Task<T> QueryAsync<T>(Func<IPlaybackQuery, Task<T>> query)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<IPlaybackQuery>());
    }

    private async Task DrainOutboxAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var relay = scope.ServiceProvider.GetRequiredService<OutboxRelay>();
        while (await relay.ProcessBatchAsync() > 0)
        {
        }
    }

    private sealed class EventSink
    {
        public ConcurrentBag<Guid> Started { get; } = [];

        public ConcurrentBag<Guid> Completed { get; } = [];

        public ConcurrentBag<Guid> TranscodeStarted { get; } = [];
    }

    private sealed class StartedSink(EventSink sink) : IEventHandler<PlaybackStarted>
    {
        public Task HandleAsync(PlaybackStarted domainEvent, CancellationToken cancellationToken = default)
        {
            sink.Started.Add(domainEvent.SessionId);
            return Task.CompletedTask;
        }
    }

    private sealed class CompletedSink(EventSink sink) : IEventHandler<PlaybackCompleted>
    {
        public Task HandleAsync(PlaybackCompleted domainEvent, CancellationToken cancellationToken = default)
        {
            sink.Completed.Add(domainEvent.SessionId);
            return Task.CompletedTask;
        }
    }

    private sealed class TranscodeStartedSink(EventSink sink) : IEventHandler<TranscodeStarted>
    {
        public Task HandleAsync(TranscodeStarted domainEvent, CancellationToken cancellationToken = default)
        {
            sink.TranscodeStarted.Add(domainEvent.SessionId);
            return Task.CompletedTask;
        }
    }
}
