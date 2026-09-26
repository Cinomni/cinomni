using Cinomni.Playback.Contracts;
using Cinomni.Playback.Encoding;

namespace Cinomni.Playback.Tests;

/// <summary>
/// Pure unit tests for the argv <see cref="FfmpegCommand"/> builds — process invocation itself is a
/// deployment concern, on the same precedent as <c>FfmpegHardwareCapabilityProbeTests</c>.
/// </summary>
public sealed class FfmpegMediaEncoderTests
{
    private const string DevicePath = "/dev/dri/renderD128";
    private const string Fit720 = "w='min(iw,1280)':h='min(ih,720)':force_original_aspect_ratio=decrease:force_divisible_by=2";

    private static TranscodeRequest Request(
        EncoderBackend backend = EncoderBackend.Software,
        bool accelerateDecode = false,
        string videoCodec = "libx264",
        int? maxWidth = null,
        int? maxHeight = null,
        int? maxBitrateKbps = null) =>
        new("/lib/film.mkv", "/tx/session", videoCodec, videoCodec == "copy" ? "copy" : "aac", backend, accelerateDecode,
            AudioStreamIndex: 1, SubtitleStreamIndex: null, maxWidth, maxHeight, maxBitrateKbps);

    private static IReadOnlyList<string> Args(TranscodeRequest request, EncoderBackend? backend = null) =>
        FfmpegCommand.Transcode(request, backend ?? request.Backend, segmentSeconds: 6, DevicePath);

    /// <summary>The value following <paramref name="flag"/> in the argv.</summary>
    private static string After(IReadOnlyList<string> args, string flag)
    {
        var index = args.ToList().IndexOf(flag);
        Assert.True(index >= 0, $"'{flag}' is not in: {string.Join(' ', args)}");
        return args[index + 1];
    }

    [Theory]
    [InlineData(EncoderBackend.Software, VideoOutputCodec.H264, "libx264")]
    [InlineData(EncoderBackend.Software, VideoOutputCodec.Hevc, "libx265")]
    [InlineData(EncoderBackend.Vaapi, VideoOutputCodec.Hevc, "hevc_vaapi")]
    [InlineData(EncoderBackend.Qsv, VideoOutputCodec.H264, "h264_qsv")]
    [InlineData(EncoderBackend.Nvenc, VideoOutputCodec.Hevc, "hevc_nvenc")]
    [InlineData(EncoderBackend.Amf, VideoOutputCodec.H264, "h264_amf")]
    [InlineData(EncoderBackend.Amf, VideoOutputCodec.Hevc, "hevc_amf")]
    public void Each_backend_and_codec_has_its_own_encoder(EncoderBackend backend, VideoOutputCodec codec, string expected)
    {
        Assert.Equal(expected, FfmpegCommand.EncoderName(backend, codec));
    }

    [Fact]
    public void A_software_transcode_decodes_scales_and_encodes_in_system_memory()
    {
        var args = Args(Request(maxWidth: 1280, maxHeight: 720));

        Assert.DoesNotContain("-hwaccel", args);
        Assert.Equal($"scale={Fit720},format=yuv420p", After(args, "-vf"));
        Assert.Equal("libx264", After(args, "-c:v"));
        Assert.Equal("veryfast", After(args, "-preset"));
        Assert.Equal("23", After(args, "-crf"));
        Assert.EndsWith("seg_%03d.ts", After(args, "-hls_segment_filename"), StringComparison.Ordinal);
    }

    [Fact]
    public void Vaapi_with_hardware_decode_keeps_the_frame_on_the_device_and_scales_there()
    {
        var args = Args(Request(EncoderBackend.Vaapi, accelerateDecode: true, maxWidth: 1280, maxHeight: 720));

        Assert.Equal($"vaapi=va:{DevicePath}", After(args, "-init_hw_device"));
        Assert.Equal("vaapi", After(args, "-hwaccel_output_format"));
        Assert.Equal($"scale_vaapi={Fit720}:format=nv12", After(args, "-vf"));
        Assert.Equal("h264_vaapi", After(args, "-c:v"));
        Assert.Equal("CQP", After(args, "-rc_mode"));
    }

    [Fact]
    public void Vaapi_without_hardware_decode_uploads_software_frames_to_the_device()
    {
        var args = Args(Request(EncoderBackend.Vaapi));

        Assert.DoesNotContain("-hwaccel", args);
        Assert.Equal("format=nv12,hwupload", After(args, "-vf"));
        Assert.Equal("va", After(args, "-filter_hw_device"));
    }

    [Fact]
    public void Nvenc_with_hardware_decode_uses_cuda_frames_and_the_cuda_scaler()
    {
        var args = Args(Request(EncoderBackend.Nvenc, accelerateDecode: true, maxHeight: 1080));

        Assert.Equal("cuda", After(args, "-hwaccel"));
        Assert.Equal("cuda", After(args, "-hwaccel_output_format"));
        Assert.StartsWith("scale_cuda=", After(args, "-vf"), StringComparison.Ordinal);
        Assert.EndsWith(":format=yuv420p", After(args, "-vf"), StringComparison.Ordinal);
        Assert.Equal("p3", After(args, "-preset"));
        Assert.Equal("23", After(args, "-cq"));
    }

    [Fact]
    public void Tone_mapping_decodes_on_the_device_but_filters_in_system_memory()
    {
        // Decode is the expensive half of a 4K HEVC source, so it stays on the GPU; the frame comes down
        // for zscale/tonemap and goes back up for the encoder.
        var request = Request(EncoderBackend.Vaapi, accelerateDecode: true, maxHeight: 1080) with { ToneMap = true };

        var args = Args(request);

        Assert.Equal("vaapi", After(args, "-hwaccel"));
        Assert.DoesNotContain("-hwaccel_output_format", args);
        // Scaled first: tone mapping on float planes costs a quarter as much at 1080p as at 4K.
        var chain = After(args, "-vf");
        Assert.StartsWith("scale=", chain, StringComparison.Ordinal);
        Assert.Contains("," + FfmpegCommand.ToneMapChain(ToneMapAlgorithm.Hable) + ",", chain, StringComparison.Ordinal);
        Assert.EndsWith("format=nv12,hwupload", chain, StringComparison.Ordinal);
    }

    [Fact]
    public void A_picture_subtitle_is_burned_in_before_the_picture_is_scaled()
    {
        var request = Request(maxWidth: 1280, maxHeight: 720) with { BurnInSubtitleIndex = 21 };

        var args = Args(request);

        Assert.DoesNotContain("-vf", args);
        Assert.Equal($"[0:v:0][0:21]overlay=eof_action=pass,scale={Fit720},format=yuv420p[v]", After(args, "-filter_complex"));
        Assert.Equal("[v]", After(args, "-map"));
    }

    [Fact]
    public void Hevc_output_is_tagged_for_browsers_and_segmented_as_fragmented_mp4()
    {
        var request = Request(EncoderBackend.Nvenc, videoCodec: "libx265") with { Fmp4Segments = true };

        var args = Args(request);

        Assert.Equal("hevc_nvenc", After(args, "-c:v"));
        Assert.Equal("hvc1", After(args, "-tag:v"));
        Assert.Equal("fmp4", After(args, "-hls_segment_type"));
        Assert.Equal(FfmpegCommand.InitSegmentName, After(args, "-hls_fmp4_init_filename"));
        Assert.EndsWith("seg_%03d.m4s", After(args, "-hls_segment_filename"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_software_retry_of_a_hardware_plan_carries_no_device_options()
    {
        var args = Args(Request(EncoderBackend.Vaapi, accelerateDecode: true), EncoderBackend.Software);

        Assert.DoesNotContain("-init_hw_device", args);
        Assert.DoesNotContain("-hwaccel", args);
        Assert.Equal("libx264", After(args, "-c:v"));
        Assert.Equal("format=yuv420p", After(args, "-vf"));
    }

    [Fact]
    public void A_remux_copies_the_video_and_never_touches_a_device()
    {
        var args = Args(Request(EncoderBackend.Vaapi, accelerateDecode: true, videoCodec: "copy"));

        Assert.Equal("copy", After(args, "-c:v"));
        Assert.Equal("copy", After(args, "-c:a"));
        Assert.DoesNotContain("-init_hw_device", args);
        Assert.DoesNotContain("-vf", args);
    }

    [Fact]
    public void Audio_is_folded_to_the_planned_channels_at_the_operators_bitrate()
    {
        var request = Request() with { AudioChannels = 2, Tuning = EncodeTuning.Default with { AudioBitrateKbps = 160 } };

        var args = Args(request);

        Assert.Equal("aac", After(args, "-c:a"));
        Assert.Equal("160k", After(args, "-b:a"));
        Assert.Equal("2", After(args, "-ac"));
    }

    [Theory]
    [InlineData(EncoderBackend.Software, new[] { "-maxrate", "4000k", "-bufsize", "8000k" })]
    [InlineData(EncoderBackend.Nvenc, new[] { "-b:v", "4000k", "-maxrate", "4000k", "-bufsize", "8000k" })]
    [InlineData(EncoderBackend.Vaapi, new[] { "-rc_mode", "VBR", "-b:v", "4000k" })]
    [InlineData(EncoderBackend.Amf, new[] { "-rc", "vbr_peak", "-b:v", "4000k" })]
    public void A_bitrate_ceiling_caps_every_encoder(EncoderBackend backend, string[] expected)
    {
        var args = FfmpegCommand.RateControl(backend, VideoOutputCodec.H264, EncodeTuning.Default, maxBitrateKbps: 4000);

        var joined = string.Join(' ', args);
        Assert.Contains(string.Join(' ', expected), joined, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_segment_starts_on_a_keyframe()
    {
        var args = Args(Request());

        Assert.Equal("expr:gte(t,n_forced*6)", After(args, "-force_key_frames"));
    }

    [Fact]
    public void A_height_alone_leaves_the_width_to_follow_the_aspect_ratio()
    {
        Assert.Equal(
            "w=iw:h='min(ih,1080)':force_original_aspect_ratio=decrease:force_divisible_by=2",
            FfmpegCommand.ScaleOptions(maxWidth: null, maxHeight: 1080));
        Assert.Null(FfmpegCommand.ScaleOptions(null, null));
    }

    [Fact]
    public void A_decode_test_keeps_the_frame_on_the_device_so_a_silent_software_fallback_fails_it()
    {
        var args = FfmpegCommand.DecodeTest(EncoderBackend.Nvenc, VideoOutputCodec.H264, "/tmp/sample.mp4", DevicePath);

        Assert.NotNull(args);
        Assert.Equal("cuda", After(args, "-hwaccel_output_format"));
        Assert.Equal("scale_cuda=format=yuv420p", After(args, "-vf"));
        Assert.Null(FfmpegCommand.DecodeTest(EncoderBackend.Software, VideoOutputCodec.H264, "/tmp/sample.mp4", DevicePath));
    }
}
