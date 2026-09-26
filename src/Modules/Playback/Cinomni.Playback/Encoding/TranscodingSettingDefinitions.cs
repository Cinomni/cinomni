using Cinomni.Operations.Settings;

namespace Cinomni.Playback.Encoding;

/// <summary>
/// The settable-key catalogue backed by <see cref="TranscodingOptions"/>. Enumerations are closed sets
/// of lower-case names; numbers are bounded. A value the settings layer refused never reaches here, and
/// one that reads as unknown falls back to the default rather than guessing.
/// </summary>
public static class TranscodingSettingDefinitions
{
    public const string Original = "original";
    public const string Unlimited = "0";

    public static readonly SettingDefinition HardwareBackend = Enum(
        "playback.hardwareBackend", "Playback:Transcoding:HardwareBackend", "auto", ["auto", "vaapi", "qsv", "nvenc", "amf"]);

    public static readonly SettingDefinition HardwareDecoding = new(
        "playback.hardwareDecoding", SettingKind.Boolean, IsSecret: false,
        "Playback:Transcoding:HardwareDecoding", "true", new SettingValidation(Required: true));

    public static readonly SettingDefinition OutputCodec = Enum(
        "playback.outputCodec", "Playback:Transcoding:OutputCodec", "h264", ["h264", "hevc-when-supported"]);

    public static readonly SettingDefinition Preset = Enum(
        "playback.encoderPreset", "Playback:Transcoding:Preset", "fast", ["fastest", "fast", "balanced", "quality"]);

    public static readonly SettingDefinition Quality = Number(
        "playback.videoQuality", "Playback:Transcoding:Quality", TranscodingOptions.DefaultQuality, 15, 40);

    public static readonly SettingDefinition MaxResolution = Enum(
        "playback.maxResolution", "Playback:Transcoding:MaxResolution", Original, [Original, "2160", "1440", "1080", "720", "480"]);

    public static readonly SettingDefinition MaxBitrateKbps = Number(
        "playback.maxBitrateKbps", "Playback:Transcoding:MaxBitrateKbps", 0, 0, 200_000);

    public static readonly SettingDefinition Threads = Number(
        "playback.encoderThreads", "Playback:Transcoding:Threads", 0, 0, 64);

    public static readonly SettingDefinition ToneMapping = new(
        "playback.toneMapping", SettingKind.Boolean, IsSecret: false,
        "Playback:Transcoding:ToneMapping", "true", new SettingValidation(Required: true));

    public static readonly SettingDefinition ToneMapAlgorithm = Enum(
        "playback.toneMapAlgorithm", "Playback:Transcoding:ToneMapAlgorithm", "hable", ["hable", "reinhard", "mobius", "bt2390"]);

    public static readonly SettingDefinition MaxAudioChannels = Enum(
        "playback.maxAudioChannels", "Playback:Transcoding:MaxAudioChannels", Original, [Original, "6", "2"]);

    public static readonly SettingDefinition AudioBitrateKbps = Number(
        "playback.audioBitrateKbps", "Playback:Transcoding:AudioBitrateKbps", TranscodingOptions.DefaultAudioBitrateKbps, 64, 640);

    public static readonly SettingDefinition BurnInImageSubtitles = new(
        "playback.burnInImageSubtitles", SettingKind.Boolean, IsSecret: false,
        "Playback:Transcoding:BurnInImageSubtitles", "true", new SettingValidation(Required: true));

    public static IReadOnlyList<SettingDefinition> All { get; } =
    [
        HardwareBackend, HardwareDecoding, OutputCodec, Preset, Quality, MaxResolution, MaxBitrateKbps, Threads,
        ToneMapping, ToneMapAlgorithm, MaxAudioChannels, AudioBitrateKbps, BurnInImageSubtitles,
    ];

    /// <summary>Reads the options from the effective settings, falling back per field to the default.</summary>
    public static TranscodingOptions Bind(SettingsView view)
    {
        var defaults = TranscodingOptions.Default;
        return new TranscodingOptions
        {
            HardwareBackend = view.GetString(HardwareBackend) switch
            {
                "vaapi" => HardwareBackendPreference.Vaapi,
                "qsv" => HardwareBackendPreference.Qsv,
                "nvenc" => HardwareBackendPreference.Nvenc,
                "amf" => HardwareBackendPreference.Amf,
                _ => HardwareBackendPreference.Auto,
            },
            HardwareDecoding = view.GetBool(HardwareDecoding) ?? defaults.HardwareDecoding,
            OutputCodec = view.GetString(OutputCodec) == "hevc-when-supported"
                ? OutputCodecPreference.HevcWhenSupported
                : OutputCodecPreference.H264,
            Preset = view.GetString(Preset) switch
            {
                "fastest" => EncoderPreset.Fastest,
                "balanced" => EncoderPreset.Balanced,
                "quality" => EncoderPreset.Quality,
                _ => EncoderPreset.Fast,
            },
            Quality = Clamp(view.GetInt(Quality), 15, 40) ?? defaults.Quality,
            MaxHeight = int.TryParse(view.GetString(MaxResolution), out var height) && height > 0 ? height : null,
            MaxBitrateKbps = view.GetInt(MaxBitrateKbps) is > 0 and var kbps ? Math.Min(kbps, 200_000) : null,
            Threads = Clamp(view.GetInt(Threads), 0, 64) ?? defaults.Threads,
            ToneMapping = view.GetBool(ToneMapping) ?? defaults.ToneMapping,
            ToneMapAlgorithm = view.GetString(ToneMapAlgorithm) switch
            {
                "reinhard" => Encoding.ToneMapAlgorithm.Reinhard,
                "mobius" => Encoding.ToneMapAlgorithm.Mobius,
                "bt2390" => Encoding.ToneMapAlgorithm.Bt2390,
                _ => Encoding.ToneMapAlgorithm.Hable,
            },
            MaxAudioChannels = view.GetString(MaxAudioChannels) switch
            {
                "6" => 6,
                "2" => 2,
                _ => null,
            },
            AudioBitrateKbps = Clamp(view.GetInt(AudioBitrateKbps), 64, 640) ?? defaults.AudioBitrateKbps,
            BurnInImageSubtitles = view.GetBool(BurnInImageSubtitles) ?? defaults.BurnInImageSubtitles,
        };
    }

    private static int? Clamp(int? value, int min, int max) => value is { } v ? Math.Clamp(v, min, max) : null;

    private static SettingDefinition Enum(string key, string path, string fallback, IReadOnlyList<string> allowed) =>
        new(key, SettingKind.Enum, IsSecret: false, path, fallback, new SettingValidation(Required: true, AllowedValues: allowed));

    private static SettingDefinition Number(string key, string path, int fallback, int min, int max) =>
        new(key, SettingKind.Number, IsSecret: false, path, fallback.ToString(System.Globalization.CultureInfo.InvariantCulture),
            new SettingValidation(Required: true, MinValue: min, MaxValue: max));
}
