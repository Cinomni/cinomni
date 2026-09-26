using Cinomni.Catalog.Contracts;
using Cinomni.Kernel.Identifiers;
using Cinomni.Kernel.Results;
using Cinomni.Kernel.Security;
using Cinomni.Library.Contracts;
using Cinomni.Operations.Settings;
using Cinomni.Playback.Application;
using Cinomni.Playback.Contracts;
using Cinomni.Playback.Encoding;
using Cinomni.Subtitles.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Playback.Tests;

/// <summary>
/// What a viewer may choose when a session opens — the audio track, a quality, where to start — and
/// the subtitle tracks a session serves as WebVTT, against a real PostgreSQL instance with the encoder,
/// the subtitle converter and the Subtitles read model faked.
/// </summary>
public sealed class PlaybackChoicesTests : IAsyncLifetime
{
    private const int EnglishAudio = 1;
    private const int SpanishAudio = 2;
    private const int EnglishText = 3;
    private const int EnglishPictures = 4;

    private static readonly long TenMinutes = TimeSpan.FromMinutes(10).Ticks;

    private readonly FakeMediaEncoder _encoder = new();
    private readonly FakeSubtitleConverter _converter = new();
    private readonly FakeSubtitleQuery _subtitles = new();
    private readonly FakeVideoRangeProbe _videoRange = new();
    private readonly string _libraryDirectory = Path.Combine(Path.GetTempPath(), "cinomni-choices-" + Guid.NewGuid().ToString("N"));
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_libraryDirectory);
        _provider = await PlaybackTestHost.CreateAsync("cinomni_test_playback_choices", _encoder, services =>
        {
            services.AddSingleton<ISubtitleConverter>(_converter);
            services.AddSingleton<ISubtitleQuery>(_subtitles);
            services.AddSingleton<IVideoRangeProbe>(_videoRange);
        });
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        Directory.Delete(_libraryDirectory, recursive: true);
    }

    [Fact]
    public async Task The_ticket_lists_the_tracks_and_the_qualities_the_file_reaches()
    {
        var assetId = await RegisterAsync();

        var ticket = await RequestAsync(Uuid7.New(), assetId);

        var media = Assert.IsType<PlaybackMediaView>(ticket.Media);
        Assert.Equal([EnglishAudio, SpanishAudio], media.AudioTracks.Select(t => t.StreamIndex));
        Assert.Equal([EnglishText, EnglishPictures], media.SubtitleTracks.Select(t => t.StreamIndex));
        Assert.True(media.SubtitleTracks.Single(t => t.StreamIndex == EnglishText).CanDisplay);
        Assert.False(media.SubtitleTracks.Single(t => t.StreamIndex == EnglishPictures).CanDisplay);
        Assert.Equal(["original", "1080p", "720p", "480p", "360p"], media.Qualities.Select(q => q.Id));
        Assert.Equal("original", media.Quality);
        Assert.Equal(TimeSpan.FromHours(2).Ticks, media.DurationTicks);
        Assert.Equal(PlaybackMethod.DirectPlay, ticket.Method);
    }

    [Fact]
    public async Task Choosing_an_audio_track_that_is_not_the_default_repackages_the_file_with_that_track()
    {
        var assetId = await RegisterAsync();

        var ticket = await RequestAsync(Uuid7.New(), assetId, new PlaybackPreferences(AudioStreamIndex: SpanishAudio));

        Assert.Equal(PlaybackMethod.Remux, ticket.Method);
        Assert.Equal(SpanishAudio, ticket.Selection.AudioStreamIndex);
        Assert.Contains(ticket.Plan.TranscodeReasons, r => r.Contains("audio track", StringComparison.Ordinal));
        Assert.Equal(SpanishAudio, Assert.Single(_encoder.Requests).AudioStreamIndex);
    }

    [Fact]
    public async Task A_lower_quality_converts_the_file_down_to_that_size_and_bitrate()
    {
        var assetId = await RegisterAsync();

        var ticket = await RequestAsync(Uuid7.New(), assetId, new PlaybackPreferences(Quality: "720p"));

        Assert.Equal(PlaybackMethod.Transcode, ticket.Method);
        Assert.Equal("720p", ticket.Media?.Quality);
        Assert.Contains(ticket.Plan.Decisions, d => d.Property == "viewerQuality" && d.Verdict == "fail");
        Assert.Contains(ticket.Plan.Decisions, d => d.Property == "viewerBitrate" && d.Actual == "20000 kbps");
        var request = Assert.Single(_encoder.Requests);
        Assert.Equal((1280, 720, 4000), (request.MaxWidth, request.MaxHeight, request.MaxBitrateKbps));
    }

    [Fact]
    public async Task A_conversion_starts_where_the_viewer_asked_and_says_where_its_zero_is()
    {
        var assetId = await RegisterAsync();

        var ticket = await RequestAsync(
            Uuid7.New(), assetId, new PlaybackPreferences(Quality: "480p", StartPositionTicks: TenMinutes));

        Assert.Equal(TenMinutes, ticket.ResumePositionTicks);
        Assert.Equal(TenMinutes, ticket.Media?.StreamOffsetTicks);
        Assert.Equal(600, Assert.Single(_encoder.Requests).StartSeconds);
    }

    [Fact]
    public async Task A_file_played_as_is_starts_at_zero_and_the_player_seeks()
    {
        var assetId = await RegisterAsync();

        var ticket = await RequestAsync(Uuid7.New(), assetId, new PlaybackPreferences(StartPositionTicks: TenMinutes));

        Assert.Equal(PlaybackMethod.DirectPlay, ticket.Method);
        Assert.Equal(TenMinutes, ticket.ResumePositionTicks);
        Assert.Equal(0, ticket.Media?.StreamOffsetTicks);
    }

    [Theory]
    [InlineData(99, null, null)]
    [InlineData(EnglishText, null, null)]
    [InlineData(null, EnglishAudio, null)]
    [InlineData(null, null, "4k-hdr")]
    public async Task A_track_or_quality_the_file_does_not_have_is_refused(int? audio, int? subtitle, string? quality)
    {
        var assetId = await RegisterAsync();

        var result = await TryRequestAsync(
            Uuid7.New(), assetId, new PlaybackPreferences(audio, subtitle, Quality: quality));

        Assert.True(result.IsFailure);
        Assert.Equal(PlaybackErrors.InvalidPreference, result.Error.Code);
        Assert.Empty(_encoder.Requests);
    }

    [Fact]
    public async Task The_next_session_remembers_the_tracks_the_viewer_chose_last_time()
    {
        var assetId = await RegisterAsync();
        var userId = Uuid7.New();
        var first = await RequestAsync(
            userId, assetId, new PlaybackPreferences(AudioStreamIndex: SpanishAudio, SubtitlesOff: true));
        await using (var scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IPlaybackSessionCommands>()
                .ReportProgressAsync(Watcher(userId), first.SessionId, TenMinutes, TimeSpan.FromHours(2).Ticks, isPaused: false);
        }

        var next = await RequestAsync(userId, assetId);

        Assert.Equal(new StreamSelectionView(SpanishAudio, null), next.Selection);
    }

    [Fact]
    public async Task Subtitles_switched_in_the_player_are_what_the_title_remembers()
    {
        var assetId = await RegisterAsync();
        var userId = Uuid7.New();
        var first = await RequestAsync(userId, assetId);
        Assert.Null(first.Selection.SubtitleStreamIndex);

        await using (var scope = _provider.CreateAsyncScope())
        {
            var commands = scope.ServiceProvider.GetRequiredService<IPlaybackSessionCommands>();
            Assert.False(await commands.ChooseSubtitleAsync(Watcher(userId), first.SessionId, EnglishAudio));
            Assert.False(await commands.ChooseSubtitleAsync(Watcher(Uuid7.New()), first.SessionId, EnglishText));
            Assert.True(await commands.ChooseSubtitleAsync(Watcher(userId), first.SessionId, EnglishText));
            await commands.ReportProgressAsync(Watcher(userId), first.SessionId, TenMinutes, TimeSpan.FromHours(2).Ticks, isPaused: true);
        }

        var next = await RequestAsync(userId, assetId);

        Assert.Equal(EnglishText, next.Selection.SubtitleStreamIndex);
    }

    [Fact]
    public async Task An_embedded_text_track_is_served_as_WebVTT()
    {
        var assetId = await RegisterAsync();
        var userId = Uuid7.New();
        var ticket = await RequestAsync(userId, assetId);

        var vtt = await SubtitleAsync(userId, ticket.SessionId, EnglishText);

        Assert.Equal(FakeSubtitleConverter.Output, vtt);
        Assert.Equal((VideoPath(), EnglishText), Assert.Single(_converter.Extractions));
    }

    [Fact]
    public async Task A_picture_track_or_somebody_elses_session_serves_nothing()
    {
        var assetId = await RegisterAsync();
        var userId = Uuid7.New();
        var ticket = await RequestAsync(userId, assetId);

        Assert.Null(await SubtitleAsync(userId, ticket.SessionId, EnglishPictures));
        Assert.Null(await SubtitleAsync(userId, ticket.SessionId, EnglishAudio));
        Assert.Null(await SubtitleAsync(Uuid7.New(), ticket.SessionId, EnglishText));
        Assert.Empty(_converter.Extractions);
    }

    [Fact]
    public async Task A_fetched_SubRip_beside_the_video_is_served_as_WebVTT()
    {
        var assetId = await RegisterAsync();
        var sidecar = Path.Combine(_libraryDirectory, "Movie.2024.spa.srt");
        await File.WriteAllBytesAsync(sidecar, [.. "1\r\n00:00:01,000 --> 00:00:02,500\r\nAdi"u8, 0xF3, (byte)'s', (byte)'\r', (byte)'\n']);
        var index = await AddSidecarAsync(assetId, sidecar);
        var userId = Uuid7.New();
        var ticket = await RequestAsync(userId, assetId);

        var vtt = await SubtitleAsync(userId, ticket.SessionId, index);

        Assert.Equal("WEBVTT\n\n1\n00:00:01.000 --> 00:00:02.500\nAdiós\n\n", vtt);
    }

    [Fact]
    public async Task A_sidecar_path_outside_the_video_directory_is_not_read()
    {
        var assetId = await RegisterAsync();
        var elsewhere = Path.Combine(Path.GetTempPath(), "cinomni-elsewhere-" + Guid.NewGuid().ToString("N") + ".srt");
        await File.WriteAllTextAsync(elsewhere, "1\n00:00:01,000 --> 00:00:02,000\nsecret\n");
        try
        {
            var index = await AddSidecarAsync(assetId, elsewhere);
            var userId = Uuid7.New();
            var ticket = await RequestAsync(userId, assetId);

            Assert.Null(await SubtitleAsync(userId, ticket.SessionId, index));
        }
        finally
        {
            File.Delete(elsewhere);
        }
    }

    [Fact]
    public async Task A_picture_subtitle_the_viewer_picks_is_burned_into_a_conversion()
    {
        Publish(new HardwareCapabilities([]) { SubtitleOverlay = true });
        var assetId = await RegisterAsync();

        var ticket = await RequestAsync(Uuid7.New(), assetId, new PlaybackPreferences(SubtitleStreamIndex: EnglishPictures));

        Assert.Equal(PlaybackMethod.Transcode, ticket.Method);
        Assert.Equal(EnglishPictures, ticket.Plan.BurnInSubtitleIndex);
        Assert.Contains(ticket.Plan.Decisions, d => d.Property == "imageSubtitle" && d.Verdict == "ok");
        var media = Assert.IsType<PlaybackMediaView>(ticket.Media);
        Assert.Equal(EnglishPictures, media.BurnedInSubtitle);
        Assert.True(media.SubtitleTracks.Single(t => t.StreamIndex == EnglishPictures).CanBurnIn);
        // A text track is shown by the player, never burned in.
        Assert.False(media.SubtitleTracks.Single(t => t.StreamIndex == EnglishText).CanBurnIn);
        var request = Assert.Single(_encoder.Requests);
        Assert.Equal(EnglishPictures, request.BurnInSubtitleIndex);
        Assert.Equal("libx264", request.VideoCodec);
    }

    [Fact]
    public async Task A_picture_subtitle_the_file_flags_as_default_does_not_force_a_conversion()
    {
        Publish(new HardwareCapabilities([]) { SubtitleOverlay = true });
        var assetId = await RegisterAsync(picturesAreDefault: true);

        var ticket = await RequestAsync(Uuid7.New(), assetId);

        Assert.Equal(EnglishPictures, ticket.Selection.SubtitleStreamIndex);
        Assert.Equal(PlaybackMethod.DirectPlay, ticket.Method);
        Assert.Null(ticket.Plan.BurnInSubtitleIndex);
        Assert.Null(ticket.Media?.BurnedInSubtitle);
        Assert.Empty(_encoder.Requests);
    }

    [Fact]
    public async Task A_picture_subtitle_is_not_burned_in_when_the_build_cannot_or_the_operator_turned_it_off()
    {
        var assetId = await RegisterAsync();

        // No overlay filter in this build: the choice is explained and the file still plays as it is.
        var withoutFilter = await RequestAsync(Uuid7.New(), assetId, new PlaybackPreferences(SubtitleStreamIndex: EnglishPictures));
        Assert.Equal(PlaybackMethod.DirectPlay, withoutFilter.Method);
        Assert.Contains(withoutFilter.Plan.Decisions, d => d.Property == "imageSubtitle" && d.Verdict == "fail");
        Assert.False(withoutFilter.Media!.SubtitleTracks.Single(t => t.StreamIndex == EnglishPictures).CanBurnIn);

        Publish(new HardwareCapabilities([]) { SubtitleOverlay = true });
        await SetAsync((TranscodingSettingDefinitions.BurnInImageSubtitles.Key, "false"));

        var turnedOff = await RequestAsync(Uuid7.New(), assetId, new PlaybackPreferences(SubtitleStreamIndex: EnglishPictures));
        Assert.Equal(PlaybackMethod.DirectPlay, turnedOff.Method);
        Assert.Contains(turnedOff.Plan.TranscodeReasons, r => r.Contains("turned off in settings", StringComparison.Ordinal));
        Assert.False(turnedOff.Media!.SubtitleTracks.Single(t => t.StreamIndex == EnglishPictures).CanBurnIn);
        Assert.Empty(_encoder.Requests);
    }

    [Fact]
    public async Task An_HDR_file_converted_for_the_viewer_is_tone_mapped_to_SDR()
    {
        Publish(new HardwareCapabilities([]) { ToneMapping = true });
        var assetId = await RegisterAsync(range: VideoRangeType.Hdr10);

        var ticket = await RequestAsync(Uuid7.New(), assetId, new PlaybackPreferences(Quality: "720p"));

        Assert.Equal(PlaybackMethod.Transcode, ticket.Method);
        Assert.True(ticket.Plan.ToneMapped);
        Assert.Contains(ticket.Plan.Decisions, d => d.Property == "dynamicRange" && d.Actual == "Hdr10" && d.Verdict == "ok");
        Assert.True(Assert.Single(_encoder.Requests).ToneMap);
        // Library knew the range, so the file itself was never asked.
        Assert.Empty(_videoRange.Probed);
    }

    [Fact]
    public async Task An_HDR_file_played_as_it_is_is_not_tone_mapped()
    {
        Publish(new HardwareCapabilities([]) { ToneMapping = true });
        var assetId = await RegisterAsync(range: VideoRangeType.Hdr10);

        var ticket = await RequestAsync(Uuid7.New(), assetId);

        Assert.Equal(PlaybackMethod.DirectPlay, ticket.Method);
        Assert.False(ticket.Plan.ToneMapped);
        Assert.Empty(_encoder.Requests);
    }

    [Fact]
    public async Task A_file_whose_range_Library_does_not_know_is_asked_and_its_answer_is_planned_on()
    {
        Publish(new HardwareCapabilities([]) { ToneMapping = true });
        _videoRange.Range = VideoRangeType.DoVi;
        var assetId = await RegisterAsync(range: null);

        var ticket = await RequestAsync(Uuid7.New(), assetId, new PlaybackPreferences(Quality: "720p"));

        Assert.True(ticket.Plan.ToneMapped);
        Assert.Contains(ticket.Plan.Decisions, d => d.Property == "dynamicRange" && d.Actual == "DoVi");
        Assert.True(Assert.Single(_encoder.Requests).ToneMap);
        Assert.Equal(VideoPath(), Assert.Single(_videoRange.Probed));
    }

    [Fact]
    public async Task The_operators_audio_settings_reach_the_encoder()
    {
        await SetAsync(
            (TranscodingSettingDefinitions.MaxAudioChannels.Key, "2"),
            (TranscodingSettingDefinitions.AudioBitrateKbps.Key, "256"),
            (TranscodingSettingDefinitions.Quality.Key, "28"),
            (TranscodingSettingDefinitions.Preset.Key, "quality"));
        var assetId = await RegisterAsync();

        var ticket = await RequestAsync(Uuid7.New(), assetId, new PlaybackPreferences(Quality: "720p"));

        Assert.Equal(2, ticket.Plan.AudioChannels);
        Assert.Contains(ticket.Plan.TranscodeReasons, r => r.Contains("folded from 6 to 2 channels", StringComparison.Ordinal));
        var request = Assert.Single(_encoder.Requests);
        Assert.Equal(2, request.AudioChannels);
        Assert.Equal(256, request.Tuning.AudioBitrateKbps);
        Assert.Equal(28, request.Tuning.Quality);
        Assert.Equal(EncoderPreset.Quality, request.Tuning.Preset);
    }

    [Fact]
    public async Task A_server_resolution_ceiling_converts_a_file_above_it_without_the_viewer_asking()
    {
        await SetAsync((TranscodingSettingDefinitions.MaxResolution.Key, "720"));
        var assetId = await RegisterAsync();

        var ticket = await RequestAsync(Uuid7.New(), assetId);

        Assert.Equal(PlaybackMethod.Transcode, ticket.Method);
        Assert.Contains(ticket.Plan.Decisions, d => d.Property == "serverMaxHeight" && d.Verdict == "fail");
        Assert.Equal(720, Assert.Single(_encoder.Requests).MaxHeight);
    }

    [Fact]
    public async Task HEVC_is_produced_in_fragmented_MP4_when_allowed_played_and_encoded_in_hardware()
    {
        Publish(new HardwareCapabilities([EncoderBackend.Vaapi])
        {
            EncodableCodecs =
            [
                new EncodeCapability(EncoderBackend.Vaapi, VideoOutputCodec.H264),
                new EncodeCapability(EncoderBackend.Vaapi, VideoOutputCodec.Hevc),
            ],
        });
        await SetAsync((TranscodingSettingDefinitions.OutputCodec.Key, "hevc-when-supported"));
        var assetId = await RegisterAsync();

        var ticket = await TryRequestAsync(
            Uuid7.New(), assetId, new PlaybackPreferences(Quality: "720p"), Client("h264", "hevc"));

        Assert.True(ticket.IsSuccess);
        Assert.Equal(VideoOutputCodec.Hevc, ticket.Value.Plan.OutputCodec);
        var request = Assert.Single(_encoder.Requests);
        Assert.Equal("libx265", request.VideoCodec);
        Assert.Equal(VideoOutputCodec.Hevc, request.OutputCodec);
        Assert.Equal(EncoderBackend.Vaapi, request.Backend);
        Assert.True(request.Fmp4Segments);
    }

    [Fact]
    public async Task HEVC_is_not_produced_for_a_client_that_does_not_list_it()
    {
        Publish(new HardwareCapabilities([EncoderBackend.Vaapi])
        {
            EncodableCodecs = [new EncodeCapability(EncoderBackend.Vaapi, VideoOutputCodec.Hevc)],
        });
        await SetAsync((TranscodingSettingDefinitions.OutputCodec.Key, "hevc-when-supported"));
        var assetId = await RegisterAsync();

        var ticket = await RequestAsync(Uuid7.New(), assetId, new PlaybackPreferences(Quality: "720p"));

        Assert.Equal(VideoOutputCodec.H264, ticket.Plan.OutputCodec);
        var request = Assert.Single(_encoder.Requests);
        Assert.Equal("libx264", request.VideoCodec);
        Assert.False(request.Fmp4Segments);
    }

    [Fact]
    public async Task A_repackaged_HEVC_source_is_copied_into_fragmented_MP4()
    {
        var assetId = await RegisterAsync(videoCodec: "hevc");

        var ticket = await TryRequestAsync(
            Uuid7.New(), assetId, preferences: null, new ClientCapability(["mp4"], ["hevc"], ["aac", "ac3"], MaxHeight: null));

        Assert.True(ticket.IsSuccess);
        Assert.Equal(PlaybackMethod.Remux, ticket.Value.Method);
        var request = Assert.Single(_encoder.Requests);
        Assert.Equal("copy", request.VideoCodec);
        Assert.True(request.Fmp4Segments);
    }

    // -- helpers ---------------------------------------------------------------------------------

    private string VideoPath() => Path.Combine(_libraryDirectory, "Movie.2024.mkv");

    private void Publish(HardwareCapabilities capabilities) =>
        _provider.GetRequiredService<HardwareCapabilitiesCache>().Publish(capabilities);

    /// <summary>Writes operator settings through the real settings store, which publishes them to every live option.</summary>
    private async Task SetAsync(params (string Key, string Value)[] settings)
    {
        await using var scope = _provider.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<ISettingsAdministration>()
            .ApplyAsync([.. settings.Select(s => SettingChange.Set(s.Key, s.Value, 0))], Uuid7.New());
        Assert.True(result.IsSuccess, string.Join("; ", result.Errors.Select(e => $"{e.Code}: {e.Message}")));
    }

    private static Viewer Watcher(Guid userId) => new(userId, IsAdministrator: false);

    private static ClientCapability Client(params string[] videoCodecs) =>
        new(["matroska"], videoCodecs.Length == 0 ? ["h264"] : videoCodecs, ["aac", "ac3"], MaxHeight: null);

    private async Task<Guid> RegisterAsync(
        VideoRangeType? range = null, bool picturesAreDefault = false, string videoCodec = "h264")
    {
        Guid workId;
        await using (var scope = _provider.CreateAsyncScope())
        {
            workId = (await scope.ServiceProvider.GetRequiredService<ICatalogCommands>().AddMovieAsync("Movie", 2024, [])).Value.Value;
        }

        var assetId = Uuid7.New();
        var request = new RegisterMediaAssetRequest(
            assetId,
            workId,
            TargetIds: [Uuid7.New()],
            FullPath: VideoPath(),
            Size: 18_000_000_000,
            Container: "matroska",
            Streams:
            [
                new MediaStreamInput(0, MediaStreamType.Video, videoCodec, null, null, 1920, 1080, null, range, true, false),
                new MediaStreamInput(EnglishAudio, MediaStreamType.Audio, "aac", "eng", 6, null, null, null, null, true, false),
                new MediaStreamInput(SpanishAudio, MediaStreamType.Audio, "ac3", "spa", 6, null, null, null, null, false, false),
                new MediaStreamInput(EnglishText, MediaStreamType.Subtitle, "subrip", "eng", null, null, null, null, null, false, false),
                new MediaStreamInput(EnglishPictures, MediaStreamType.Subtitle, "hdmv_pgs_subtitle", "eng", null, null, null, null, null, picturesAreDefault, false),
            ],
            DurationSeconds: 7200,
            Bitrate: 20_000_000);
        await using (var scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ILibraryCommands>().RegisterMediaAssetAsync(request);
        }

        return assetId;
    }

    /// <summary>Registers a fetched Spanish sidecar on the asset, as the Subtitles module would, and returns its stream index.</summary>
    private async Task<int> AddSidecarAsync(Guid assetId, string path)
    {
        _subtitles.Obtained.Add(new SubtitleAssetSummary(
            new SubtitleAssetId(Uuid7.New()), assetId, "spa", Forced: false, HearingImpaired: false,
            SubtitleFormat.Srt, path, "test", Score: 90));
        await using var scope = _provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ILibraryCommands>().AddExternalSubtitleAsync(assetId, "spa", forced: false);
        var asset = await scope.ServiceProvider.GetRequiredService<ILibraryQuery>().GetAsync(new MediaAssetId(assetId));
        return asset!.Versions[0].Streams.Single(s => s.IsExternal).StreamIndex;
    }

    private async Task<Result<PlaybackTicket>> TryRequestAsync(
        Guid userId, Guid assetId, PlaybackPreferences? preferences, ClientCapability? client = null)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IPlaybackSessionCommands>()
            .RequestPlaybackAsync(Watcher(userId), assetId, client ?? Client(), preferences);
    }

    private async Task<PlaybackTicket> RequestAsync(Guid userId, Guid assetId, PlaybackPreferences? preferences = null)
    {
        var result = await TryRequestAsync(userId, assetId, preferences);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
        return result.Value;
    }

    private async Task<string?> SubtitleAsync(Guid userId, PlaybackSessionId sessionId, int index)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PlaybackSubtitles>()
            .GetWebVttAsync(Watcher(userId), sessionId, index);
    }

    private sealed class FakeSubtitleConverter : ISubtitleConverter
    {
        public const string Output = "WEBVTT\n\n00:00:01.000 --> 00:00:02.000\nHello\n";

        public List<(string Path, int Index)> Extractions { get; } = [];

        public Task<string?> ExtractToWebVttAsync(string mediaPath, int streamIndex, CancellationToken cancellationToken = default)
        {
            Extractions.Add((mediaPath, streamIndex));
            return Task.FromResult<string?>(Output);
        }

        public Task<string?> AssToWebVttAsync(string assText, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(Output);
    }

    /// <summary>Answers what the file's header would, and records which files were asked.</summary>
    private sealed class FakeVideoRangeProbe : IVideoRangeProbe
    {
        public VideoRangeType? Range { get; set; }

        public List<string> Probed { get; } = [];

        public Task<VideoRangeType?> ProbeAsync(Guid versionId, string path, CancellationToken cancellationToken = default)
        {
            Probed.Add(path);
            return Task.FromResult(Range);
        }
    }

    private sealed class FakeSubtitleQuery : ISubtitleQuery
    {
        public List<SubtitleAssetSummary> Obtained { get; } = [];

        public Task<IReadOnlyList<SubtitleSearchSummary>> ListForAssetAsync(Guid assetId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SubtitleSearchSummary>>([]);

        public Task<SubtitleSearchDetail?> GetAsync(SubtitleSearchId searchId, CancellationToken cancellationToken = default) =>
            Task.FromResult<SubtitleSearchDetail?>(null);

        public Task<IReadOnlyList<SubtitleAssetSummary>> ListObtainedAsync(Guid assetId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SubtitleAssetSummary>>(Obtained.Where(s => s.AssetId == assetId).ToList());
    }
}
