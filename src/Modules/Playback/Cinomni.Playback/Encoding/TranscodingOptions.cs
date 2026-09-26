using Cinomni.Playback.Contracts;

namespace Cinomni.Playback.Encoding;

/// <summary>Which hardware backend a transcode should prefer: the first detected one, or a named one.</summary>
public enum HardwareBackendPreference
{
    Auto = 1,
    Vaapi = 2,
    Qsv = 3,
    Nvenc = 4,
    Amf = 5,
}

/// <summary>Which video codec a transcode produces: always H.264, or HEVC where the client and the encoder both can.</summary>
public enum OutputCodecPreference
{
    H264 = 1,
    HevcWhenSupported = 2,
}

/// <summary>Speed against compression — mapped onto each encoder's own preset vocabulary.</summary>
public enum EncoderPreset
{
    Fastest = 1,
    Fast = 2,
    Balanced = 3,
    Quality = 4,
}

/// <summary>The curve that brings HDR brightness into SDR's range.</summary>
public enum ToneMapAlgorithm
{
    Hable = 1,
    Reinhard = 2,
    Mobius = 3,
    Bt2390 = 4,
}

/// <summary>
/// How this installation transcodes — the operator's knobs for fitting the server to its hardware and
/// its network, all live (Console → Settings → Playback) and read on every plan. Every value reaches
/// FFmpeg only through a closed switch or as a bounded integer, never as free text.
/// </summary>
public sealed record TranscodingOptions
{
    /// <summary>The hardware backend to prefer; <see cref="HardwareBackendPreference.Auto"/> takes the first one detected.</summary>
    public HardwareBackendPreference HardwareBackend { get; init; } = HardwareBackendPreference.Auto;

    /// <summary>Whether a hardware backend may also decode the source, not only encode the output.</summary>
    public bool HardwareDecoding { get; init; } = true;

    public OutputCodecPreference OutputCodec { get; init; } = OutputCodecPreference.H264;

    public EncoderPreset Preset { get; init; } = EncoderPreset.Fast;

    /// <summary>Constant-quality target (CRF/CQ/QP): lower is better and bigger. 23 is a sensible middle.</summary>
    public int Quality { get; init; } = DefaultQuality;

    /// <summary>The tallest picture any transcode produces; null for no server-wide ceiling.</summary>
    public int? MaxHeight { get; init; }

    /// <summary>The highest video bitrate any stream is sent at, in kbps; null for no ceiling.</summary>
    public int? MaxBitrateKbps { get; init; }

    /// <summary>Software encoder threads; 0 lets FFmpeg decide.</summary>
    public int Threads { get; init; }

    /// <summary>Whether an HDR source is brought to SDR when it is transcoded.</summary>
    public bool ToneMapping { get; init; } = true;

    public ToneMapAlgorithm ToneMapAlgorithm { get; init; } = ToneMapAlgorithm.Hable;

    /// <summary>The most audio channels a transcode keeps (2 folds surround to stereo); null keeps them all.</summary>
    public int? MaxAudioChannels { get; init; }

    public int AudioBitrateKbps { get; init; } = DefaultAudioBitrateKbps;

    /// <summary>Whether a picture subtitle (PGS, VobSub) the viewer picks is burned into the video.</summary>
    public bool BurnInImageSubtitles { get; init; } = true;

    public const int DefaultQuality = 23;
    public const int DefaultAudioBitrateKbps = 192;

    public static TranscodingOptions Default { get; } = new();

    /// <summary>The backend a preference names, or null for <see cref="HardwareBackendPreference.Auto"/>.</summary>
    public EncoderBackend? PreferredBackend => HardwareBackend switch
    {
        HardwareBackendPreference.Vaapi => EncoderBackend.Vaapi,
        HardwareBackendPreference.Qsv => EncoderBackend.Qsv,
        HardwareBackendPreference.Nvenc => EncoderBackend.Nvenc,
        HardwareBackendPreference.Amf => EncoderBackend.Amf,
        _ => null,
    };
}
