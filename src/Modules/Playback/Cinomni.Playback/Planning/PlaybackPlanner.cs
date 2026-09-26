using Cinomni.Operations.Settings;
using Cinomni.Playback.Contracts;
using Cinomni.Playback.Encoding;

namespace Cinomni.Playback.Planning;

/// <summary>
/// The default plan decision: Direct Play when the client supports the container and every codec;
/// Remux when only the container is unsupported (repackage, no re-encode); Transcode when a codec or
/// the resolution is beyond the client. Every check is recorded as a decision line so the plan is
/// fully explainable. A Transcode plan also picks an encoder backend from the
/// host's detected hardware, unless an operator turned hardware transcoding off
/// (<see cref="HardwareTranscodingOptions"/>); Direct Play and Remux never re-encode video, so they
/// carry <see cref="EncoderBackend.Software"/> with no acceleration reasons. When a hardware backend
/// is picked, the plan also records whether that backend can decode the source's own codec in
/// hardware, not just encode the output — the two are separate capabilities (see
/// <see cref="IEncoderBackendSelector"/>).
/// <para>
/// The viewer's own choices are inputs too, explained like the rest: a quality below the file's
/// converts it down to that rung's size and bitrate, an audio track other than the file's default
/// needs at least a repackage, and a picture subtitle can only be seen burned into the video.
/// </para>
/// <para>
/// So are the operator's (<see cref="TranscodingOptions"/>): a server-wide resolution or bitrate
/// ceiling the file is above forces a transcode; a transcode of an HDR source is tone-mapped to SDR;
/// HEVC is produced when the operator allows it, the client plays it and a hardware backend encodes
/// it; surround audio is folded to the channel count they set.
/// </para>
/// </summary>
public sealed class PlaybackPlanner(
    IEncoderBackendSelector backendSelector,
    HardwareCapabilitiesCache hardwareCapabilities,
    ILiveOptions<HardwareTranscodingOptions> hardwareTranscoding,
    ILiveOptions<TranscodingOptions> transcoding)
    : IPlaybackPlanner
{
    private const string Ok = "ok";
    private const string Fail = "fail";

    public PlaybackPlan Plan(PlaybackSource source, ClientCapability capability, QualityStep? quality = null)
    {
        var options = transcoding.Current;
        var capabilities = hardwareCapabilities.Current;
        var decisions = new List<PlaybackDecision>();
        var reasons = new List<string>();

        var containerOk = Supports(capability.Containers, source.Container);
        decisions.Add(new PlaybackDecision(
            "container", Join(capability.Containers), source.Container, Verdict(containerOk)));

        var videoCodecOk = source.VideoCodec is null || Supports(capability.VideoCodecs, source.VideoCodec);
        decisions.Add(new PlaybackDecision(
            "videoCodec", Join(capability.VideoCodecs), source.VideoCodec ?? "none", Verdict(videoCodecOk)));
        if (!videoCodecOk)
        {
            reasons.Add($"video codec '{source.VideoCodec}' not supported");
        }

        var resolutionOk = capability.MaxHeight is not { } max || source.VideoHeight is not { } height || height <= max;
        decisions.Add(new PlaybackDecision(
            "videoHeight",
            capability.MaxHeight?.ToString() ?? "any",
            source.VideoHeight?.ToString() ?? "unknown",
            Verdict(resolutionOk)));
        if (!resolutionOk)
        {
            reasons.Add($"resolution {source.VideoHeight} exceeds client max {capability.MaxHeight}");
        }

        var unsupportedAudio = source.AudioCodecs.Where(c => !Supports(capability.AudioCodecs, c)).ToList();
        var audioOk = unsupportedAudio.Count == 0;
        decisions.Add(new PlaybackDecision(
            "audioCodec", Join(capability.AudioCodecs), Join(source.AudioCodecs), Verdict(audioOk)));
        foreach (var codec in unsupportedAudio)
        {
            reasons.Add($"audio codec '{codec}' not supported");
        }

        var qualityOk = quality is null || CheckQuality(source, quality, decisions, reasons);
        var serverOk = CheckServerCeilings(source, options, decisions, reasons);
        var burnIn = CheckImageSubtitle(source, options, capabilities, decisions, reasons);

        decisions.Add(new PlaybackDecision(
            "audioTrack", "file default", source.SelectedAudioIsDefault ? "file default" : "viewer's choice",
            Verdict(source.SelectedAudioIsDefault)));

        var mustReencode = !videoCodecOk || !resolutionOk || !audioOk || !qualityOk || !serverOk || burnIn is not null;
        var method = mustReencode
            ? PlaybackMethod.Transcode
            : containerOk && source.SelectedAudioIsDefault ? PlaybackMethod.DirectPlay : PlaybackMethod.Remux;

        if (method == PlaybackMethod.Remux)
        {
            // Not a re-encode reason, but the reason a repackage is needed — recorded for the explanation.
            reasons.Add(containerOk
                ? "the chosen audio track is not the file's default (remux)"
                : $"container '{source.Container}' not supported (remux)");
        }

        if (method != PlaybackMethod.Transcode)
        {
            return new PlaybackPlan(method, decisions, reasons, EncoderBackend.Software, [], DecodeAccelerated: false);
        }

        return PlanTranscode(source, capability, quality, options, capabilities, decisions, reasons, burnIn, resolutionOk);
    }

    private PlaybackPlan PlanTranscode(
        PlaybackSource source,
        ClientCapability capability,
        QualityStep? quality,
        TranscodingOptions options,
        HardwareCapabilities capabilities,
        List<PlaybackDecision> decisions,
        List<string> reasons,
        int? burnIn,
        bool resolutionOk)
    {
        var codec = ChooseOutputCodec(capability, options, capabilities, reasons);
        var selection = SelectBackend(source.VideoCodec, options, codec);

        var toneMap = false;
        if (source.IsHdr)
        {
            toneMap = options.ToneMapping && capabilities.ToneMapping;
            decisions.Add(new PlaybackDecision("dynamicRange", "SDR", source.VideoRange?.ToString() ?? "HDR", Verdict(toneMap)));
            reasons.Add(!options.ToneMapping
                ? "the source is HDR and tone mapping is off in settings; colours may look washed out"
                : toneMap
                    ? $"the source is {source.VideoRange} HDR; it is tone-mapped to SDR while it is converted"
                    : "the source is HDR but this FFmpeg build has no tone-mapping filters; colours may look washed out");
        }

        var audioChannels = options.MaxAudioChannels is { } channels && source.AudioChannels is { } sourceChannels && sourceChannels > channels
            ? channels
            : (int?)null;
        if (audioChannels is not null)
        {
            reasons.Add($"audio is folded from {source.AudioChannels} to {audioChannels} channels (server setting)");
        }

        return new PlaybackPlan(
            PlaybackMethod.Transcode,
            decisions,
            reasons,
            selection.Backend,
            selection.AccelerationReasons,
            selection.DecodeAccelerated,
            TargetMaxWidth: quality?.MaxWidth,
            TargetMaxHeight: Min(Min(quality?.MaxHeight, resolutionOk ? null : capability.MaxHeight), options.MaxHeight),
            TargetBitrateKbps: Min(quality?.MaxBitrateKbps, options.MaxBitrateKbps),
            OutputCodec: codec,
            ToneMapped: toneMap,
            BurnInSubtitleIndex: burnIn,
            AudioChannels: audioChannels);
    }

    /// <summary>
    /// HEVC when the operator allows it, the client says it plays it, and a hardware backend on this
    /// host encodes it. Software x265 is too slow for a stream a viewer is waiting on, so without such a
    /// backend the transcode stays H.264 — explained, not silent.
    /// </summary>
    private VideoOutputCodec ChooseOutputCodec(
        ClientCapability capability, TranscodingOptions options, HardwareCapabilities capabilities, List<string> reasons)
    {
        if (options.OutputCodec != OutputCodecPreference.HevcWhenSupported)
        {
            return VideoOutputCodec.H264;
        }

        if (!Supports(capability.VideoCodecs, "hevc"))
        {
            reasons.Add("HEVC output is allowed but this client does not play HEVC; converting to H.264");
            return VideoOutputCodec.H264;
        }

        var hardwareHevc = hardwareTranscoding.Current.Enabled
            && capabilities.AvailableBackends.Any(b => capabilities.CanEncode(b, VideoOutputCodec.Hevc));
        if (!hardwareHevc)
        {
            reasons.Add("HEVC output is allowed but no hardware backend on this host encodes it; converting to H.264");
            return VideoOutputCodec.H264;
        }

        reasons.Add("converting to HEVC: the client plays it and the hardware encodes it, at a lower bitrate than H.264");
        return VideoOutputCodec.Hevc;
    }

    /// <summary>
    /// Whether the file is within the operator's server-wide ceilings. A bitrate the probe could not
    /// measure is not held against the file here — unlike a viewer's explicit choice, a server ceiling
    /// should not convert every file whose bitrate is unknown.
    /// </summary>
    private static bool CheckServerCeilings(
        PlaybackSource source, TranscodingOptions options, List<PlaybackDecision> decisions, List<string> reasons)
    {
        var ok = true;
        if (options.MaxHeight is { } maxHeight)
        {
            var heightOk = source.VideoHeight is not { } height || height <= maxHeight;
            decisions.Add(new PlaybackDecision(
                "serverMaxHeight", maxHeight.ToString(), source.VideoHeight?.ToString() ?? "unknown", Verdict(heightOk)));
            if (!heightOk)
            {
                reasons.Add($"resolution {source.VideoHeight} exceeds the server's maximum of {maxHeight}");
                ok = false;
            }
        }

        if (options.MaxBitrateKbps is { } maxKbps)
        {
            var sourceKbps = source.Bitrate is { } bits ? bits / 1000 : (long?)null;
            var bitrateOk = sourceKbps is not { } kbps || kbps <= maxKbps;
            decisions.Add(new PlaybackDecision(
                "serverMaxBitrate", $"{maxKbps} kbps", sourceKbps is { } known ? $"{known} kbps" : "unknown", Verdict(bitrateOk)));
            if (!bitrateOk)
            {
                reasons.Add($"the file's {sourceKbps} kbps exceeds the server's maximum of {maxKbps} kbps");
                ok = false;
            }
        }

        return ok;
    }

    /// <summary>
    /// The picture subtitle to burn in, or null. A browser cannot draw PGS or VobSub, so a viewer who
    /// picked one sees it only in a converted stream — when the operator allows that and the build has
    /// the overlay filter.
    /// </summary>
    private static int? CheckImageSubtitle(
        PlaybackSource source,
        TranscodingOptions options,
        HardwareCapabilities capabilities,
        List<PlaybackDecision> decisions,
        List<string> reasons)
    {
        if (source.ImageSubtitleIndex is not { } index)
        {
            return null;
        }

        var burn = options.BurnInImageSubtitles && capabilities.SubtitleOverlay;
        decisions.Add(new PlaybackDecision("imageSubtitle", "burn in", $"stream {index}", Verdict(burn)));
        reasons.Add(burn
            ? "the chosen subtitles are pictures the browser cannot draw; they are burned into the video"
            : "the chosen subtitles are pictures the browser cannot draw, and burning them in is "
                + (options.BurnInImageSubtitles ? "not possible with this FFmpeg build" : "turned off in settings"));
        return burn ? index : null;
    }

    /// <summary>
    /// Whether the file already fits the quality the viewer chose, recording both halves: its size
    /// against the rung's box, and its bitrate against the rung's. A bitrate the probe could not measure
    /// does not fit — the viewer asked for a ceiling, and only a conversion can promise one.
    /// </summary>
    private static bool CheckQuality(
        PlaybackSource source, QualityStep quality, List<PlaybackDecision> decisions, List<string> reasons)
    {
        var sizeOk = !quality.IsExceededBy(source.VideoWidth, source.VideoHeight);
        decisions.Add(new PlaybackDecision(
            "viewerQuality",
            $"{quality.MaxWidth}x{quality.MaxHeight}",
            $"{source.VideoWidth?.ToString() ?? "?"}x{source.VideoHeight?.ToString() ?? "?"}",
            Verdict(sizeOk)));
        if (!sizeOk)
        {
            reasons.Add($"viewer chose {quality.Id}: the picture is scaled down to fit {quality.MaxWidth}x{quality.MaxHeight}");
        }

        var sourceKbps = source.Bitrate is { } bits ? bits / 1000 : (long?)null;
        var bitrateOk = sourceKbps is { } kbps && kbps <= quality.MaxBitrateKbps;
        decisions.Add(new PlaybackDecision(
            "viewerBitrate",
            $"{quality.MaxBitrateKbps} kbps",
            sourceKbps is { } known ? $"{known} kbps" : "unknown",
            Verdict(bitrateOk)));
        if (!bitrateOk)
        {
            reasons.Add(sourceKbps is null
                ? $"viewer chose {quality.Id}: the file's bitrate is unknown, so it is converted to stay under {quality.MaxBitrateKbps} kbps"
                : $"viewer chose {quality.Id}: the file's {sourceKbps} kbps is above {quality.MaxBitrateKbps} kbps");
        }

        return sizeOk && bitrateOk;
    }

    private EncoderBackendSelection SelectBackend(string? sourceVideoCodec, TranscodingOptions options, VideoOutputCodec codec)
    {
        if (!hardwareTranscoding.Current.Enabled)
        {
            return new EncoderBackendSelection(
                EncoderBackend.Software,
                ["hardware transcoding is disabled by operator setting; transcoding uses software"],
                DecodeAccelerated: false);
        }

        return backendSelector.Select(hardwareCapabilities.Current, sourceVideoCodec, options, codec);
    }

    private static int? Min(int? a, int? b) => a is null ? b : b is null ? a : Math.Min(a.Value, b.Value);

    private static bool Supports(IReadOnlyList<string> supported, string value) =>
        supported.Any(s => string.Equals(s, value, StringComparison.OrdinalIgnoreCase));

    private static string Join(IReadOnlyList<string> values) => values.Count == 0 ? "none" : string.Join(",", values);

    private static string Verdict(bool ok) => ok ? Ok : Fail;
}
