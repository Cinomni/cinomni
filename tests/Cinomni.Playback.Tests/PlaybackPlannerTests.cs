using Cinomni.Library.Contracts;
using Cinomni.Operations.Settings;
using Cinomni.Playback.Contracts;
using Cinomni.Playback.Encoding;
using Cinomni.Playback.Planning;

namespace Cinomni.Playback.Tests;

/// <summary>Pure unit tests for the explainable playback planner (no FFmpeg — decisions only).</summary>
public sealed class PlaybackPlannerTests
{
    private readonly HardwareCapabilitiesCache _hardwareCapabilities = new();
    private readonly MutableLiveOptions<HardwareTranscodingOptions> _hardwareTranscoding = new(new HardwareTranscodingOptions());
    private readonly MutableLiveOptions<TranscodingOptions> _transcoding = new(TranscodingOptions.Default);
    private readonly PlaybackPlanner _planner;

    public PlaybackPlannerTests() =>
        _planner = new PlaybackPlanner(new EncoderBackendSelector(), _hardwareCapabilities, _hardwareTranscoding, _transcoding);

    private static PlaybackSource Mkv(string videoCodec = "h264", int height = 1080, string audioCodec = "aac") =>
        new("matroska", videoCodec, height, [audioCodec]);

    private static ClientCapability Client(
        string[]? containers = null,
        string[]? video = null,
        string[]? audio = null,
        int? maxHeight = null) =>
        new(containers ?? ["matroska", "mp4"], video ?? ["h264"], audio ?? ["aac"], maxHeight);

    [Fact]
    public void Direct_play_when_container_and_codecs_are_supported()
    {
        var plan = _planner.Plan(Mkv(), Client());

        Assert.Equal(PlaybackMethod.DirectPlay, plan.Method);
        Assert.Empty(plan.TranscodeReasons);
        Assert.Contains(plan.Decisions, d => d.Property == "container" && d.Verdict == "ok");
    }

    [Fact]
    public void Direct_play_never_selects_an_encoder_backend_even_when_hardware_is_available()
    {
        // No video is re-encoded, so a detected accelerator is irrelevant to this plan.
        _hardwareCapabilities.Publish(new HardwareCapabilities([EncoderBackend.Vaapi]));

        var plan = _planner.Plan(Mkv(), Client());

        Assert.Equal(EncoderBackend.Software, plan.Backend);
        Assert.Empty(plan.AccelerationReasons);
    }

    [Fact]
    public void Remux_when_only_the_container_is_unsupported()
    {
        // Client plays h264/aac but not the matroska container → repackage, no re-encode.
        var plan = _planner.Plan(Mkv(), Client(containers: ["mp4"]));

        Assert.Equal(PlaybackMethod.Remux, plan.Method);
        Assert.Contains(plan.Decisions, d => d.Property == "container" && d.Verdict == "fail");
        Assert.Equal(EncoderBackend.Software, plan.Backend);
        Assert.Empty(plan.AccelerationReasons);
    }

    [Fact]
    public void Transcode_when_a_video_codec_is_unsupported()
    {
        var plan = _planner.Plan(Mkv(videoCodec: "hevc"), Client(video: ["h264"]));

        Assert.Equal(PlaybackMethod.Transcode, plan.Method);
        Assert.Contains(plan.TranscodeReasons, r => r.Contains("hevc"));
    }

    [Fact]
    public void Transcode_falls_back_to_software_when_no_hardware_backend_was_detected()
    {
        var plan = _planner.Plan(Mkv(videoCodec: "hevc"), Client(video: ["h264"]));

        Assert.Equal(EncoderBackend.Software, plan.Backend);
        Assert.Contains(plan.AccelerationReasons, r => r.Contains("no hardware encoder backend"));
    }

    [Fact]
    public void Transcode_selects_the_detected_hardware_backend_and_explains_why()
    {
        _hardwareCapabilities.Publish(new HardwareCapabilities([EncoderBackend.Nvenc]));

        var plan = _planner.Plan(Mkv(videoCodec: "hevc"), Client(video: ["h264"]));

        Assert.Equal(EncoderBackend.Nvenc, plan.Backend);
        Assert.Contains(plan.AccelerationReasons, r => r.Contains("nvenc", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Transcode_reports_decode_accelerated_when_the_selected_backend_can_decode_the_source_codec()
    {
        _hardwareCapabilities.Publish(new HardwareCapabilities(
            [EncoderBackend.Vaapi], [new DecodeCapability(EncoderBackend.Vaapi, "hevc")]));

        var plan = _planner.Plan(Mkv(videoCodec: "hevc"), Client(video: ["h264"]));

        Assert.Equal(EncoderBackend.Vaapi, plan.Backend);
        Assert.True(plan.DecodeAccelerated);
    }

    [Fact]
    public void Transcode_reports_decode_not_accelerated_when_the_backend_has_no_matching_decode_capability()
    {
        // Vaapi is detected for encode, but nothing here says it can decode hevc — a plausible split
        // on real hardware (see EncoderBackendSelectorTests for the pure decision this wraps).
        _hardwareCapabilities.Publish(new HardwareCapabilities([EncoderBackend.Vaapi]));

        var plan = _planner.Plan(Mkv(videoCodec: "hevc"), Client(video: ["h264"]));

        Assert.Equal(EncoderBackend.Vaapi, plan.Backend);
        Assert.False(plan.DecodeAccelerated);
    }

    [Fact]
    public void Direct_play_never_reports_decode_accelerated_even_when_the_backend_could_decode_the_source()
    {
        _hardwareCapabilities.Publish(new HardwareCapabilities(
            [EncoderBackend.Vaapi], [new DecodeCapability(EncoderBackend.Vaapi, "h264")]));

        var plan = _planner.Plan(Mkv(), Client());

        Assert.Equal(PlaybackMethod.DirectPlay, plan.Method);
        Assert.False(plan.DecodeAccelerated);
    }

    [Fact]
    public void Transcode_uses_software_when_the_operator_disabled_hardware_transcoding()
    {
        // Hardware is detected, but the operator's switch overrides the selector entirely.
        _hardwareCapabilities.Publish(new HardwareCapabilities([EncoderBackend.Vaapi]));
        _hardwareTranscoding.Current = new HardwareTranscodingOptions { Enabled = false };

        var plan = _planner.Plan(Mkv(videoCodec: "hevc"), Client(video: ["h264"]));

        Assert.Equal(EncoderBackend.Software, plan.Backend);
        Assert.Contains(plan.AccelerationReasons, r => r.Contains("disabled"));
    }

    [Fact]
    public void Transcode_when_resolution_exceeds_the_client_max()
    {
        var plan = _planner.Plan(Mkv(height: 2160), Client(maxHeight: 1080));

        Assert.Equal(PlaybackMethod.Transcode, plan.Method);
        Assert.Contains(plan.TranscodeReasons, r => r.Contains("exceeds"));
        Assert.Contains(plan.Decisions, d => d.Property == "videoHeight" && d.Verdict == "fail");
    }

    [Fact]
    public void Transcode_when_an_audio_codec_is_unsupported()
    {
        var plan = _planner.Plan(Mkv(audioCodec: "dts"), Client(audio: ["aac"]));

        Assert.Equal(PlaybackMethod.Transcode, plan.Method);
        Assert.Contains(plan.TranscodeReasons, r => r.Contains("dts"));
    }

    [Fact]
    public void The_plan_records_a_decision_for_every_property()
    {
        var plan = _planner.Plan(Mkv(), Client());

        var properties = plan.Decisions.Select(d => d.Property).ToList();
        Assert.Contains("container", properties);
        Assert.Contains("videoCodec", properties);
        Assert.Contains("videoHeight", properties);
        Assert.Contains("audioCodec", properties);
    }

    [Fact]
    public void The_plan_round_trips_through_json()
    {
        _hardwareCapabilities.Publish(new HardwareCapabilities(
            [EncoderBackend.Nvenc], [new DecodeCapability(EncoderBackend.Nvenc, "hevc")]));
        var plan = _planner.Plan(Mkv(videoCodec: "hevc"), Client(video: ["h264"]));

        var restored = PlaybackPlan.FromJson(plan.ToJson());

        Assert.Equal(plan.Method, restored.Method);
        Assert.Equal(plan.Decisions.Count, restored.Decisions.Count);
        Assert.Equal(plan.TranscodeReasons, restored.TranscodeReasons);
        Assert.Equal(plan.Backend, restored.Backend);
        Assert.Equal(plan.AccelerationReasons, restored.AccelerationReasons);
        Assert.Equal(plan.DecodeAccelerated, restored.DecodeAccelerated);
        Assert.True(restored.DecodeAccelerated);
    }

    private static readonly QualityStep Q720 = QualityLadder.Steps.Single(s => s.Id == "720p");

    private static PlaybackSource Film(int width = 1920, int height = 1080, long? bitrate = 20_000_000, bool defaultAudio = true) =>
        new("matroska", "h264", height, ["aac"], width, bitrate, defaultAudio);

    [Fact]
    public void A_quality_below_the_file_transcodes_to_that_rungs_box_and_bitrate()
    {
        var plan = _planner.Plan(Film(), Client(), Q720);

        Assert.Equal(PlaybackMethod.Transcode, plan.Method);
        Assert.Equal((1280, 720, 4000), (plan.TargetMaxWidth, plan.TargetMaxHeight, plan.TargetBitrateKbps));
        Assert.Contains(plan.Decisions, d => d is { Property: "viewerQuality", Verdict: "fail" });
        Assert.Contains(plan.TranscodeReasons, r => r.StartsWith("viewer chose 720p", StringComparison.Ordinal));
    }

    [Fact]
    public void A_file_that_already_fits_the_quality_plays_as_it_is()
    {
        var plan = _planner.Plan(Film(1280, 536, bitrate: 3_000_000), Client(), Q720);

        Assert.Equal(PlaybackMethod.DirectPlay, plan.Method);
        Assert.Null(plan.TargetBitrateKbps);
        Assert.Contains(plan.Decisions, d => d is { Property: "viewerBitrate", Verdict: "ok" });
    }

    [Fact]
    public void A_wide_film_is_measured_by_its_width_as_well_as_its_height()
    {
        // 1920x800 is under 1080 lines but still far wider than the 720p box.
        var plan = _planner.Plan(Film(1920, 800, bitrate: 1_000_000), Client(), Q720);

        Assert.Equal(PlaybackMethod.Transcode, plan.Method);
    }

    [Fact]
    public void An_unknown_bitrate_cannot_promise_the_ceiling_so_it_transcodes()
    {
        var plan = _planner.Plan(Film(1280, 720, bitrate: null), Client(), Q720);

        Assert.Equal(PlaybackMethod.Transcode, plan.Method);
        Assert.Contains(plan.Decisions, d => d is { Property: "viewerBitrate", Actual: "unknown", Verdict: "fail" });
    }

    [Fact]
    public void An_audio_track_other_than_the_default_needs_a_remux_even_in_a_supported_container()
    {
        var plan = _planner.Plan(Film(defaultAudio: false), Client());

        Assert.Equal(PlaybackMethod.Remux, plan.Method);
        Assert.Contains(plan.TranscodeReasons, r => r.Contains("audio track", StringComparison.Ordinal));
    }

    [Fact]
    public void A_client_screen_ceiling_is_carried_as_the_transcode_target_height()
    {
        var plan = _planner.Plan(Mkv(height: 2160), Client(maxHeight: 1080));

        Assert.Equal(PlaybackMethod.Transcode, plan.Method);
        Assert.Equal(1080, plan.TargetMaxHeight);
        Assert.Null(plan.TargetMaxWidth);
    }

    [Fact]
    public void A_plan_saved_before_the_targets_existed_reads_them_as_uncapped()
    {
        var restored = PlaybackPlan.FromJson("""{"method":"Transcode","decisions":[],"transcodeReasons":[],"backend":"Software","accelerationReasons":[],"decodeAccelerated":false}""");

        Assert.Null(restored.TargetMaxHeight);
        Assert.Null(restored.TargetBitrateKbps);
    }

    [Theory]
    [InlineData(1920, 1080, new[] { "original", "1080p", "720p", "480p", "360p" })]
    [InlineData(1920, 800, new[] { "original", "1080p", "720p", "480p", "360p" })]
    [InlineData(1280, 720, new[] { "original", "720p", "480p", "360p" })]
    [InlineData(640, 360, new[] { "original", "360p" })]
    public void The_ladder_offers_the_rungs_the_picture_reaches(int width, int height, string[] expected)
    {
        Assert.Equal(expected, QualityLadder.OfferedFor(width, height).Select(q => q.Id));
    }

    [Fact]
    public void An_unknown_quality_id_is_reported_as_unknown_rather_than_guessed()
    {
        Assert.Null(QualityLadder.Find("original", out var original));
        Assert.True(original);
        Assert.Null(QualityLadder.Find("8k", out var unknown));
        Assert.False(unknown);
    }

    private static readonly HardwareCapabilities FullBuild = new([])
    {
        SoftwareHevc = true,
        ToneMapping = true,
        SubtitleOverlay = true,
    };

    [Fact]
    public void A_server_resolution_ceiling_the_file_is_above_forces_a_scaled_transcode()
    {
        _transcoding.Current = TranscodingOptions.Default with { MaxHeight = 1080 };

        var plan = _planner.Plan(Mkv(height: 2160), Client());

        Assert.Equal(PlaybackMethod.Transcode, plan.Method);
        Assert.Equal(1080, plan.TargetMaxHeight);
        Assert.Contains(plan.Decisions, d => d is { Property: "serverMaxHeight", Verdict: "fail" });
    }

    [Fact]
    public void A_server_bitrate_ceiling_does_not_convert_a_file_whose_bitrate_is_unknown()
    {
        _transcoding.Current = TranscodingOptions.Default with { MaxBitrateKbps = 8000 };

        Assert.Equal(PlaybackMethod.DirectPlay, _planner.Plan(Film(bitrate: null), Client()).Method);
        var capped = _planner.Plan(Film(bitrate: 30_000_000), Client());
        Assert.Equal(PlaybackMethod.Transcode, capped.Method);
        Assert.Equal(8000, capped.TargetBitrateKbps);
    }

    [Fact]
    public void An_hdr_source_is_tone_mapped_when_it_is_transcoded_and_the_build_can()
    {
        _hardwareCapabilities.Publish(FullBuild);
        var hdr = new PlaybackSource("matroska", "hevc", 2160, ["aac"], 3840, VideoRange: VideoRangeType.DoVi);

        var plan = _planner.Plan(hdr, Client(video: ["h264"]));

        Assert.True(plan.ToneMapped);
        Assert.Contains(plan.Decisions, d => d is { Property: "dynamicRange", Actual: "DoVi", Verdict: "ok" });
    }

    [Fact]
    public void An_hdr_source_without_tone_mapping_filters_is_converted_and_the_washed_out_colours_explained()
    {
        _hardwareCapabilities.Publish(FullBuild with { ToneMapping = false });
        var hdr = new PlaybackSource("matroska", "hevc", 2160, ["aac"], VideoRange: VideoRangeType.Hdr10);

        var plan = _planner.Plan(hdr, Client(video: ["h264"]));

        Assert.False(plan.ToneMapped);
        Assert.Contains(plan.TranscodeReasons, r => r.Contains("washed out", StringComparison.Ordinal));
    }

    [Fact]
    public void An_hdr_source_played_as_is_is_left_to_the_client()
    {
        _hardwareCapabilities.Publish(FullBuild);
        var hdr = new PlaybackSource("matroska", "hevc", 2160, ["aac"], VideoRange: VideoRangeType.Hdr10);

        var plan = _planner.Plan(hdr, Client(video: ["hevc"]));

        Assert.Equal(PlaybackMethod.DirectPlay, plan.Method);
        Assert.False(plan.ToneMapped);
    }

    [Fact]
    public void A_picture_subtitle_the_viewer_picked_is_burned_in_with_a_transcode()
    {
        _hardwareCapabilities.Publish(FullBuild);

        var plan = _planner.Plan(Film() with { ImageSubtitleIndex = 21 }, Client());

        Assert.Equal(PlaybackMethod.Transcode, plan.Method);
        Assert.Equal(21, plan.BurnInSubtitleIndex);
    }

    [Fact]
    public void A_picture_subtitle_is_not_burned_in_when_the_operator_turned_it_off()
    {
        _hardwareCapabilities.Publish(FullBuild);
        _transcoding.Current = TranscodingOptions.Default with { BurnInImageSubtitles = false };

        var plan = _planner.Plan(Film() with { ImageSubtitleIndex = 21 }, Client());

        Assert.Equal(PlaybackMethod.DirectPlay, plan.Method);
        Assert.Null(plan.BurnInSubtitleIndex);
        Assert.Contains(plan.TranscodeReasons, r => r.Contains("turned off in settings", StringComparison.Ordinal));
    }

    [Fact]
    public void Hevc_is_produced_only_when_allowed_played_by_the_client_and_encoded_in_hardware()
    {
        _transcoding.Current = TranscodingOptions.Default with { OutputCodec = OutputCodecPreference.HevcWhenSupported };
        _hardwareCapabilities.Publish(new HardwareCapabilities([EncoderBackend.Vaapi])
        {
            EncodableCodecs = [new EncodeCapability(EncoderBackend.Vaapi, VideoOutputCodec.H264), new EncodeCapability(EncoderBackend.Vaapi, VideoOutputCodec.Hevc)],
        });

        var hevc = _planner.Plan(Film(height: 2160, width: 3840), Client(video: ["h264", "hevc"], maxHeight: 1080));
        var h264Client = _planner.Plan(Film(height: 2160, width: 3840), Client(video: ["h264"], maxHeight: 1080));

        Assert.Equal(VideoOutputCodec.Hevc, hevc.OutputCodec);
        Assert.Equal(EncoderBackend.Vaapi, hevc.Backend);
        Assert.Equal(VideoOutputCodec.H264, h264Client.OutputCodec);
    }

    [Fact]
    public void Hevc_is_not_produced_by_software_x265_for_a_waiting_viewer()
    {
        _transcoding.Current = TranscodingOptions.Default with { OutputCodec = OutputCodecPreference.HevcWhenSupported };
        _hardwareCapabilities.Publish(FullBuild);

        var plan = _planner.Plan(Film(height: 2160, width: 3840), Client(video: ["h264", "hevc"], maxHeight: 1080));

        Assert.Equal(VideoOutputCodec.H264, plan.OutputCodec);
        Assert.Contains(plan.TranscodeReasons, r => r.Contains("no hardware backend on this host encodes it", StringComparison.Ordinal));
    }

    [Fact]
    public void Surround_audio_is_folded_to_the_operators_channel_count_only_when_it_has_more()
    {
        _transcoding.Current = TranscodingOptions.Default with { MaxAudioChannels = 2 };

        var surround = _planner.Plan(Film() with { AudioChannels = 6 }, Client(video: ["vp9"]));
        var stereo = _planner.Plan(Film() with { AudioChannels = 2 }, Client(video: ["vp9"]));

        Assert.Equal(2, surround.AudioChannels);
        Assert.Null(stereo.AudioChannels);
    }

    /// <summary>A settable <see cref="ILiveOptions{TOptions}"/> stand-in — no SettingsCache/IConfiguration needed for a pure test.</summary>
    private sealed class MutableLiveOptions<TOptions>(TOptions initial) : ILiveOptions<TOptions>
        where TOptions : class
    {
        public TOptions Current { get; set; } = initial;
    }
}
