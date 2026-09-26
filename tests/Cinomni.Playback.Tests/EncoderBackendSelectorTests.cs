using Cinomni.Playback.Contracts;
using Cinomni.Playback.Encoding;

namespace Cinomni.Playback.Tests;

/// <summary>Pure unit tests for the encoder backend selection (no FFmpeg — capability input, decision output).</summary>
public sealed class EncoderBackendSelectorTests
{
    private readonly EncoderBackendSelector _selector = new();

    [Fact]
    public void Falls_back_to_software_when_no_hardware_backend_was_detected()
    {
        var selection = _selector.Select(new HardwareCapabilities([]), "h264");

        Assert.Equal(EncoderBackend.Software, selection.Backend);
        Assert.False(selection.DecodeAccelerated);
        Assert.Contains(selection.AccelerationReasons, r => r.Contains("no hardware encoder backend"));
    }

    [Fact]
    public void Selects_the_only_available_hardware_backend()
    {
        var selection = _selector.Select(new HardwareCapabilities([EncoderBackend.Qsv]), "h264");

        Assert.Equal(EncoderBackend.Qsv, selection.Backend);
        Assert.Contains(selection.AccelerationReasons, r => r.Contains("qsv", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Prefers_vaapi_over_nvenc_and_qsv_when_several_are_available()
    {
        var selection = _selector.Select(
            new HardwareCapabilities([EncoderBackend.Qsv, EncoderBackend.Nvenc, EncoderBackend.Vaapi]), "h264");

        Assert.Equal(EncoderBackend.Vaapi, selection.Backend);
    }

    [Fact]
    public void Prefers_nvenc_over_qsv_when_vaapi_is_unavailable()
    {
        var selection = _selector.Select(new HardwareCapabilities([EncoderBackend.Qsv, EncoderBackend.Nvenc]), "h264");

        Assert.Equal(EncoderBackend.Nvenc, selection.Backend);
    }

    [Fact]
    public void Every_selection_carries_at_least_one_reason()
    {
        var withHardware = _selector.Select(new HardwareCapabilities([EncoderBackend.Vaapi]), "h264");
        var withoutHardware = _selector.Select(new HardwareCapabilities([]), "h264");

        Assert.NotEmpty(withHardware.AccelerationReasons);
        Assert.NotEmpty(withoutHardware.AccelerationReasons);
    }

    [Fact]
    public void Reports_decode_accelerated_when_the_chosen_backend_can_decode_the_source_codec()
    {
        var capabilities = new HardwareCapabilities(
            [EncoderBackend.Vaapi], [new DecodeCapability(EncoderBackend.Vaapi, "h264")]);

        var selection = _selector.Select(capabilities, "h264");

        Assert.True(selection.DecodeAccelerated);
        Assert.Contains(selection.AccelerationReasons, r => r.Contains("also decoded"));
    }

    [Fact]
    public void Reports_decode_not_accelerated_when_the_source_codec_has_no_hardware_decoder_on_that_backend()
    {
        // Vaapi can decode h264 on this host, but the source here is hevc — a common asymmetry
        // (older VAAPI/QSV generations, or a build with only a subset of _cuvid decoders).
        var capabilities = new HardwareCapabilities(
            [EncoderBackend.Vaapi], [new DecodeCapability(EncoderBackend.Vaapi, "h264")]);

        var selection = _selector.Select(capabilities, "hevc");

        Assert.Equal(EncoderBackend.Vaapi, selection.Backend);
        Assert.False(selection.DecodeAccelerated);
        Assert.Contains(selection.AccelerationReasons, r => r.Contains("no hardware decoder"));
    }

    [Fact]
    public void Reports_decode_not_accelerated_when_the_backend_has_no_decode_capability_at_all()
    {
        // Encode capability (h264_nvenc) says nothing about decode (NVDEC/_cuvid) — the two are
        // separate driver paths, most visibly on NVIDIA.
        var selection = _selector.Select(new HardwareCapabilities([EncoderBackend.Nvenc]), "h264");

        Assert.Equal(EncoderBackend.Nvenc, selection.Backend);
        Assert.False(selection.DecodeAccelerated);
    }

    [Fact]
    public void Reports_decode_not_accelerated_when_the_source_codec_is_unknown()
    {
        var capabilities = new HardwareCapabilities(
            [EncoderBackend.Vaapi], [new DecodeCapability(EncoderBackend.Vaapi, "h264")]);

        var selection = _selector.Select(capabilities, sourceVideoCodec: null);

        Assert.False(selection.DecodeAccelerated);
    }

    [Fact]
    public void Takes_the_operators_preferred_backend_over_the_default_order()
    {
        var options = TranscodingOptions.Default with { HardwareBackend = HardwareBackendPreference.Qsv };

        var selection = _selector.Select(new HardwareCapabilities([EncoderBackend.Vaapi, EncoderBackend.Qsv]), "h264", options);

        Assert.Equal(EncoderBackend.Qsv, selection.Backend);
        Assert.Contains(selection.AccelerationReasons, r => r.Contains("operator's choice", StringComparison.Ordinal));
    }

    [Fact]
    public void Falls_back_to_the_next_detected_backend_when_the_preferred_one_is_missing_and_says_so()
    {
        var options = TranscodingOptions.Default with { HardwareBackend = HardwareBackendPreference.Amf };

        var selection = _selector.Select(new HardwareCapabilities([EncoderBackend.Nvenc]), "h264", options);

        Assert.Equal(EncoderBackend.Nvenc, selection.Backend);
        Assert.Contains(selection.AccelerationReasons, r => r.Contains("'Amf' was not detected", StringComparison.Ordinal));
    }

    [Fact]
    public void Keeps_decode_in_software_when_the_operator_turned_hardware_decoding_off()
    {
        var capabilities = new HardwareCapabilities([EncoderBackend.Vaapi], [new DecodeCapability(EncoderBackend.Vaapi, "hevc")]);
        var options = TranscodingOptions.Default with { HardwareDecoding = false };

        var selection = _selector.Select(capabilities, "hevc", options);

        Assert.False(selection.DecodeAccelerated);
        Assert.Contains(selection.AccelerationReasons, r => r.Contains("turned off in settings", StringComparison.Ordinal));
    }

    [Fact]
    public void Picks_only_a_backend_that_encodes_the_output_codec()
    {
        var capabilities = new HardwareCapabilities([EncoderBackend.Vaapi, EncoderBackend.Nvenc])
        {
            EncodableCodecs =
            [
                new EncodeCapability(EncoderBackend.Vaapi, VideoOutputCodec.H264),
                new EncodeCapability(EncoderBackend.Nvenc, VideoOutputCodec.H264),
                new EncodeCapability(EncoderBackend.Nvenc, VideoOutputCodec.Hevc),
            ],
        };

        var selection = _selector.Select(capabilities, "h264", codec: VideoOutputCodec.Hevc);

        Assert.Equal(EncoderBackend.Nvenc, selection.Backend);
    }
}
