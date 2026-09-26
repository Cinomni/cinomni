using System.Text.Json;
using System.Text.Json.Serialization;
using Cinomni.Playback.Contracts;

namespace Cinomni.Playback.Planning;

/// <summary>
/// The explainable playback plan: the chosen delivery method plus the per-property
/// decisions that justify it and the reasons a transcode was needed. Persisted as <c>jsonb</c> on the
/// session so every playback decision can be audited after the fact. The target ceilings are what a
/// transcode converts to (null: uncapped); a plan persisted before they existed reads them as null.
/// <para>
/// A transcode also records what it produces: the video codec, whether HDR was tone-mapped to SDR,
/// which picture subtitle was burned in, and the channel count the audio was folded to. All read as
/// "not done" on a plan persisted before they existed.
/// </para>
/// </summary>
public sealed record PlaybackPlan(
    PlaybackMethod Method,
    IReadOnlyList<PlaybackDecision> Decisions,
    IReadOnlyList<string> TranscodeReasons,
    EncoderBackend Backend,
    IReadOnlyList<string> AccelerationReasons,
    bool DecodeAccelerated,
    int? TargetMaxWidth = null,
    int? TargetMaxHeight = null,
    int? TargetBitrateKbps = null,
    VideoOutputCodec? OutputCodec = null,
    bool ToneMapped = false,
    int? BurnInSubtitleIndex = null,
    int? AudioChannels = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public PlaybackPlanView ToView() =>
        new(Method, Decisions, TranscodeReasons, Backend, AccelerationReasons, DecodeAccelerated,
            TargetMaxWidth, TargetMaxHeight, TargetBitrateKbps, OutputCodec, ToneMapped, BurnInSubtitleIndex, AudioChannels);

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static PlaybackPlan FromJson(string json) =>
        JsonSerializer.Deserialize<PlaybackPlan>(json, JsonOptions)
        ?? new PlaybackPlan(PlaybackMethod.DirectPlay, [], [], EncoderBackend.Software, [], DecodeAccelerated: false);
}
