using Cinomni.Playback.Contracts;
using Cinomni.Playback.Encoding;

namespace Cinomni.Playback.Tests;

/// <summary>
/// Pure unit tests for the hardware probe's parsing and backend-determination logic — the
/// process invocation itself is a deployment concern, on the same precedent as
/// <c>FfprobeMediaProbe</c>/<c>FfmpegMediaEncoder</c>. Fixtures below are synthesized from FFmpeg's
/// own documented <c>-hwaccels</c>/<c>-encoders</c> output shape, never captured from a real machine.
/// </summary>
public sealed class FfmpegHardwareCapabilityProbeTests
{
    private const string HwaccelsWithVaapiAndQsv = """
        Hardware acceleration methods:
        vdpau
        vaapi
        drm
        qsv
        """;

    private const string HwaccelsSoftwareOnly = """
        Hardware acceleration methods:
        """;

    private const string EncodersWithHardware = """
        Encoders:
         V..... = Video
         A..... = Audio
         S..... = Subtitle
         .F.... = Frame-level multithreading
         ..S... = Slice-level multithreading
         ...X.. = Codec is experimental
         ....B. = Supports draw_horiz_band
         .....D = Supports direct rendering method 1
         ------
         V..... libx264              libx264 H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10 (codecs: h264)
         V..... h264_vaapi           H.264/AVC (VAAPI) (codecs: h264)
         V..... h264_nvenc           NVIDIA NVENC H.264 encoder (codecs: h264)
         V..... h264_qsv             H264 video (Quick Sync Video acceleration) (codecs: h264)
         A..... aac                  AAC (Advanced Audio Coding)
        """;

    private const string EncodersSoftwareOnly = """
        Encoders:
         V..... = Video
         A..... = Audio
         ------
         V..... libx264              libx264 H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10 (codecs: h264)
         A..... aac                  AAC (Advanced Audio Coding)
        """;

    private const string DecodersWithHardware = """
        Decoders:
         V..... = Video
         A..... = Audio
         S..... = Subtitle
         .F.... = Frame-level multithreading
         ..S... = Slice-level multithreading
         ...X.. = Codec is experimental
         ....B. = Supports draw_horiz_band
         .....D = Supports direct rendering method 1
         ------
         V..... h264                 H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10
         V..... h264_vaapi           H.264/AVC (VAAPI) (codecs: h264)
         V..... hevc_vaapi           H.265/HEVC (VAAPI) (codecs: hevc)
         V..... h264_qsv             H264 video (Quick Sync Video acceleration) (codecs: h264)
         V..... h264_cuvid           Nvidia CUVID H264 decoder (codecs: h264)
         A..... aac                  AAC (Advanced Audio Coding)
        """;

    private const string DecodersSoftwareOnly = """
        Decoders:
         V..... = Video
         A..... = Audio
         ------
         V..... h264                 H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10
         A..... aac                  AAC (Advanced Audio Coding)
        """;

    [Fact]
    public void ParseHwaccels_reads_every_method_name_and_skips_the_header()
    {
        var hwaccels = FfmpegHardwareCapabilityProbe.ParseHwaccels(HwaccelsWithVaapiAndQsv);

        Assert.Equal(["vdpau", "vaapi", "drm", "qsv"], hwaccels);
    }

    [Fact]
    public void ParseHwaccels_returns_empty_for_a_software_only_build()
    {
        var hwaccels = FfmpegHardwareCapabilityProbe.ParseHwaccels(HwaccelsSoftwareOnly);

        Assert.Empty(hwaccels);
    }

    [Fact]
    public void ParseEncoders_reads_the_codec_name_out_of_each_row_and_skips_the_legend()
    {
        var encoders = FfmpegHardwareCapabilityProbe.ParseEncoders(EncodersWithHardware);

        Assert.Equal(["libx264", "h264_vaapi", "h264_nvenc", "h264_qsv", "aac"], encoders);
    }

    [Fact]
    public void ParseEncoders_finds_only_the_software_codecs_when_that_is_all_there_is()
    {
        var encoders = FfmpegHardwareCapabilityProbe.ParseEncoders(EncodersSoftwareOnly);

        Assert.Equal(["libx264", "aac"], encoders);
    }

    [Fact]
    public void DetermineAvailableBackends_reports_nothing_when_nothing_was_detected()
    {
        var backends = FfmpegHardwareCapabilityProbe.DetermineAvailableBackends([], ["libx264", "aac"], devicePathExists: false, nvidiaDevicePresent: true);

        Assert.Empty(backends);
    }

    [Fact]
    public void DetermineAvailableBackends_withholds_vaapi_when_the_device_path_is_missing()
    {
        var hwaccels = FfmpegHardwareCapabilityProbe.ParseHwaccels(HwaccelsWithVaapiAndQsv);
        var encoders = FfmpegHardwareCapabilityProbe.ParseEncoders(EncodersWithHardware);

        var backends = FfmpegHardwareCapabilityProbe.DetermineAvailableBackends(hwaccels, encoders, devicePathExists: false, nvidiaDevicePresent: true);

        // NVENC needs no device path (nvidia-container-toolkit handles that at the container level),
        // so it is the one backend still reported even with no /dev/dri node.
        Assert.DoesNotContain(EncoderBackend.Vaapi, backends);
        Assert.DoesNotContain(EncoderBackend.Qsv, backends);
        Assert.Contains(EncoderBackend.Nvenc, backends);
    }

    [Fact]
    public void DetermineAvailableBackends_reports_vaapi_when_the_hwaccel_the_encoder_and_the_device_all_agree()
    {
        var hwaccels = FfmpegHardwareCapabilityProbe.ParseHwaccels(HwaccelsWithVaapiAndQsv);
        var encoders = FfmpegHardwareCapabilityProbe.ParseEncoders(EncodersWithHardware);

        var backends = FfmpegHardwareCapabilityProbe.DetermineAvailableBackends(hwaccels, encoders, devicePathExists: true, nvidiaDevicePresent: true);

        Assert.Contains(EncoderBackend.Vaapi, backends);
    }

    [Fact]
    public void DetermineAvailableBackends_reports_qsv_when_the_encoder_and_the_device_agree()
    {
        var hwaccels = FfmpegHardwareCapabilityProbe.ParseHwaccels(HwaccelsWithVaapiAndQsv);
        var encoders = FfmpegHardwareCapabilityProbe.ParseEncoders(EncodersWithHardware);

        var backends = FfmpegHardwareCapabilityProbe.DetermineAvailableBackends(hwaccels, encoders, devicePathExists: true, nvidiaDevicePresent: true);

        Assert.Contains(EncoderBackend.Qsv, backends);
    }

    [Fact]
    public void DetermineAvailableBackends_reports_every_backend_available_at_once()
    {
        var hwaccels = FfmpegHardwareCapabilityProbe.ParseHwaccels(HwaccelsWithVaapiAndQsv);
        var encoders = FfmpegHardwareCapabilityProbe.ParseEncoders(EncodersWithHardware);

        var backends = FfmpegHardwareCapabilityProbe.DetermineAvailableBackends(hwaccels, encoders, devicePathExists: true, nvidiaDevicePresent: true);

        Assert.Equal([EncoderBackend.Vaapi, EncoderBackend.Qsv, EncoderBackend.Nvenc], backends);
    }

    [Fact]
    public void DetermineAvailableBackends_withholds_nvenc_when_no_nvidia_gpu_was_granted()
    {
        // FFmpeg builds carry h264_nvenc on any machine; without the GPU it can only fail.
        var hwaccels = FfmpegHardwareCapabilityProbe.ParseHwaccels(HwaccelsWithVaapiAndQsv);
        var encoders = FfmpegHardwareCapabilityProbe.ParseEncoders(EncodersWithHardware);

        var backends = FfmpegHardwareCapabilityProbe.DetermineAvailableBackends(
            hwaccels, encoders, devicePathExists: false, nvidiaDevicePresent: false);

        Assert.Empty(backends);
    }

    [Fact]
    public void ParseDecoders_reads_the_codec_name_out_of_each_row_and_skips_the_legend()
    {
        var decoders = FfmpegHardwareCapabilityProbe.ParseDecoders(DecodersWithHardware);

        Assert.Equal(["h264", "h264_vaapi", "hevc_vaapi", "h264_qsv", "h264_cuvid", "aac"], decoders);
    }

    [Fact]
    public void ParseDecoders_finds_only_the_software_codecs_when_that_is_all_there_is()
    {
        var decoders = FfmpegHardwareCapabilityProbe.ParseDecoders(DecodersSoftwareOnly);

        Assert.Equal(["h264", "aac"], decoders);
    }

    private const string FiltersWithToneMapping = """
        Filters:
          T.. = Timeline support
          .S. = Slice threading
          ..C = Command support
          A = Audio input/output
          V = Video input/output
          N = Dynamic number and/or type of input/output
          | = Source or sink filter
         TSC overlay           VV->V      Overlay a video source on top of the input.
         ..C tonemap           V->V       Conversion to/from different dynamic ranges.
         .SC zscale            V->V       Apply resizing, colorspace and bit depth conversion.
         ... scale_vaapi       V->V       Scale to/from VAAPI surfaces.
        """;

    [Fact]
    public void ParseFilters_reads_the_filter_names_and_skips_the_legend()
    {
        var filters = FfmpegHardwareCapabilityProbe.ParseFilters(FiltersWithToneMapping);

        Assert.Equal(["overlay", "tonemap", "zscale", "scale_vaapi"], filters);
    }

    [Fact]
    public void CandidateBackends_on_windows_tests_nvenc_qsv_and_amf_without_device_nodes()
    {
        string[] encoders = ["libx264", "h264_nvenc", "h264_qsv", "h264_amf", "h264_vaapi"];

        var candidates = FfmpegHardwareCapabilityProbe.CandidateBackends(
            [], encoders, devicePathExists: false, nvidiaDevicePresent: false, isWindows: true);

        Assert.Equal([EncoderBackend.Nvenc, EncoderBackend.Qsv, EncoderBackend.Amf], candidates);
    }

    [Fact]
    public void CandidateBackends_never_offers_amf_on_linux()
    {
        var candidates = FfmpegHardwareCapabilityProbe.CandidateBackends(
            ["vaapi"], ["h264_vaapi", "h264_amf"], devicePathExists: true, nvidiaDevicePresent: false, isWindows: false);

        Assert.Equal([EncoderBackend.Vaapi], candidates);
    }

    [Fact]
    public void Summarize_reports_only_what_the_hardware_actually_did()
    {
        // NVENC was listed and tried, and failed: the listing alone is not availability.
        ProbeTestResult[] tests =
        [
            new(EncoderBackend.Vaapi, ProbeTestKind.Encode, "h264", Passed: true, null),
            new(EncoderBackend.Vaapi, ProbeTestKind.Encode, "hevc", Passed: true, null),
            new(EncoderBackend.Vaapi, ProbeTestKind.Decode, "hevc", Passed: true, null),
            new(EncoderBackend.Vaapi, ProbeTestKind.Decode, "h264", Passed: false, "Failed to initialise VAAPI"),
            new(EncoderBackend.Nvenc, ProbeTestKind.Encode, "h264", Passed: false, "Cannot load libcuda.so.1"),
        ];

        var capabilities = FfmpegHardwareCapabilityProbe.Summarize(
            tests, ["libx264", "libx265"], ["zscale", "tonemap", "overlay"]);

        Assert.Equal([EncoderBackend.Vaapi], capabilities.AvailableBackends);
        Assert.True(capabilities.CanEncode(EncoderBackend.Vaapi, VideoOutputCodec.Hevc));
        Assert.False(capabilities.CanEncode(EncoderBackend.Nvenc, VideoOutputCodec.H264));
        Assert.Equal([new DecodeCapability(EncoderBackend.Vaapi, "hevc")], capabilities.DecodeCapableCodecs);
        Assert.True(capabilities.SoftwareHevc);
        Assert.True(capabilities.ToneMapping);
        Assert.True(capabilities.SubtitleOverlay);
    }

    [Fact]
    public void Summarize_without_tone_mapping_filters_says_so()
    {
        var capabilities = FfmpegHardwareCapabilityProbe.Summarize([], ["libx264"], ["tonemap"]);

        Assert.Empty(capabilities.AvailableBackends);
        Assert.False(capabilities.ToneMapping);
        Assert.False(capabilities.SoftwareHevc);
    }

    [Theory]
    [InlineData("""{"streams":[{"color_transfer":"smpte2084"}]}""", Cinomni.Library.Contracts.VideoRangeType.Hdr10)]
    [InlineData("""{"streams":[{"color_transfer":"bt709","side_data_list":[{"side_data_type":"DOVI configuration record"}]}]}""", Cinomni.Library.Contracts.VideoRangeType.DoVi)]
    [InlineData("""{"streams":[{"color_transfer":"bt709"}]}""", Cinomni.Library.Contracts.VideoRangeType.Sdr)]
    public void The_playback_range_probe_reads_what_import_would_have(string json, Cinomni.Library.Contracts.VideoRangeType expected)
    {
        Assert.Equal(expected, FfprobeVideoRangeProbe.Parse(json));
    }

    [Fact]
    public void The_playback_range_probe_knows_nothing_about_a_file_with_no_video()
    {
        Assert.Null(FfprobeVideoRangeProbe.Parse("""{"streams":[]}"""));
    }
}
