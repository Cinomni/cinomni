using Cinomni.Kernel.Identifiers;

namespace Cinomni.Playback.Contracts;

/// <summary>Stable internal identity of a playback session (UUIDv7).</summary>
public readonly record struct PlaybackSessionId(Guid Value)
{
    public static PlaybackSessionId New() => new(Uuid7.New());

    public override string ToString() => Value.ToString();
}

/// <summary>Stable internal identity of a transcode job (UUIDv7).</summary>
public readonly record struct TranscodeJobId(Guid Value)
{
    public static TranscodeJobId New() => new(Uuid7.New());

    public override string ToString() => Value.ToString();
}

/// <summary>
/// The lifecycle of a playback session (persisted). It starts, runs in one of
/// three delivery modes, may pause, and ends complete or failed. Server-side progress advances it
/// even while the client is quiet.
/// </summary>
public enum PlaybackState
{
    Starting = 1,
    DirectPlaying = 2,
    Remuxing = 3,
    Transcoding = 4,
    Paused = 5,
    Completed = 6,
    Failed = 7,
}

/// <summary>
/// How the content is delivered, decided by the planner (explainable): play the file as-is,
/// repackage the container without re-encoding, or transcode to HLS.
/// </summary>
public enum PlaybackMethod
{
    DirectPlay = 1,
    Remux = 2,
    Transcode = 3,
}

/// <summary>
/// Why a session ended — recorded when it does, so a closed session explains itself rather than only
/// saying that it is over. Wire-stable: persisted and served by name.
/// </summary>
public enum PlaybackEndReason
{
    /// <summary>The viewer got past the watched threshold.</summary>
    Watched = 1,

    /// <summary>The viewer stopped.</summary>
    Stopped = 2,

    /// <summary>Nothing asked for the stream within the idle timeout: the viewer left without saying so.</summary>
    Idle = 3,

    /// <summary>The stream reached the longest a transcode may run.</summary>
    Expired = 4,

    /// <summary>The viewer may no longer see the title.</summary>
    AccessRevoked = 5,

    /// <summary>FFmpeg could not start, or failed part way; the reason is on the transcode job.</summary>
    TranscodeFailed = 6,

    /// <summary>The server restarted while the stream was still being converted.</summary>
    ServerRestarted = 7,

    /// <summary>
    /// Its stream had already been stopped when the sweep found the session still open: the close that
    /// ended it never got to record why.
    /// </summary>
    Recovered = 8,
}

/// <summary>The lifecycle of an FFmpeg transcode job (isolated process; a failure never takes down the server).</summary>
public enum TranscodeState
{
    Preparing = 1,
    Running = 2,
    Segmenting = 3,
    Completed = 4,
    Failed = 5,
    Cleaned = 6,
}

/// <summary>
/// Which encoder implementation a transcode used. <see cref="Software"/> (libx264) is what every
/// installation has today; the hardware backends are detected at startup and only ever chosen when
/// actually available (see <c>IHardwareCapabilityProbe</c>) — this is a stable wire enum because a
/// later increment persists it on the explainable playback plan.
/// </summary>
public enum EncoderBackend
{
    Software = 1,
    Vaapi = 2,
    Nvenc = 3,
    Qsv = 4,

    /// <summary>AMD's Advanced Media Framework — only on a host running natively on Windows.</summary>
    Amf = 5,
}

/// <summary>The video codec a transcode produces. Wire-stable: persisted on the plan by name.</summary>
public enum VideoOutputCodec
{
    H264 = 1,
    Hevc = 2,
}

/// <summary>
/// What the requesting client can play natively — the inputs to the plan decision. Codec/container
/// names are lower-case (e.g. <c>h264</c>, <c>mp4</c>). Sent per request; persisting device profiles
/// is a later addition.
/// </summary>
public sealed record ClientCapability(
    IReadOnlyList<string> Containers,
    IReadOnlyList<string> VideoCodecs,
    IReadOnlyList<string> AudioCodecs,
    int? MaxHeight);

/// <summary>
/// One line of the plan's explanation: a property, the client's supported set
/// (<see cref="Expected"/>), the asset's actual value, and the verdict.
/// </summary>
public sealed record PlaybackDecision(string Property, string Expected, string Actual, string Verdict);

/// <summary>
/// The explainable playback plan: the chosen method, its per-property decisions, why a transcode is
/// needed, and which encoder backend a transcode uses. <see cref="Backend"/> is
/// <see cref="EncoderBackend.Software"/> with an empty <see cref="AccelerationReasons"/> for Direct
/// Play/Remux, since neither re-encodes video. <see cref="DecodeAccelerated"/> is a second, separate
/// fact about the same backend: whether it also decodes the source's own codec in hardware rather
/// than only encoding the output — always <c>false</c> outside <see cref="PlaybackMethod.Transcode"/>.
/// </summary>
/// <para>
/// <see cref="TargetMaxWidth"/>, <see cref="TargetMaxHeight"/> and <see cref="TargetBitrateKbps"/> are
/// the ceilings a transcode converts to — the client's screen or the quality the viewer chose — and
/// null where nothing caps the output; always null outside <see cref="PlaybackMethod.Transcode"/>.
/// So are <see cref="OutputCodec"/>, <see cref="ToneMapped"/> (HDR brought to SDR),
/// <see cref="BurnInSubtitleIndex"/> (a picture subtitle drawn into the video) and
/// <see cref="AudioChannels"/> (the channel count the audio was folded to).
/// </para>
public sealed record PlaybackPlanView(
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
    int? AudioChannels = null);

/// <summary>The audio/subtitle tracks the session will play (by stream index).</summary>
public sealed record StreamSelectionView(int? AudioStreamIndex, int? SubtitleStreamIndex);

/// <summary>
/// What the viewer asked for when opening a session; every field is optional and an absent one leaves
/// the choice to the server (the viewer's last choice for this title, else the file's defaults).
/// </summary>
/// <param name="AudioStreamIndex">The audio stream to play; must name an audio stream of the file.</param>
/// <param name="SubtitleStreamIndex">The subtitle stream shown; must name a subtitle stream of the file.</param>
/// <param name="SubtitlesOff">The viewer turned subtitles off; wins over <paramref name="SubtitleStreamIndex"/>.</param>
/// <param name="Quality">
/// The id of one of the quality options the server offers (see <see cref="QualityOptionView"/>);
/// null or <c>original</c> for the file as it is.
/// </param>
/// <param name="StartPositionTicks">
/// Where to start instead of the stored resume position — a viewer changing track or quality part way
/// through keeps their place.
/// </param>
public sealed record PlaybackPreferences(
    int? AudioStreamIndex = null,
    int? SubtitleStreamIndex = null,
    bool SubtitlesOff = false,
    string? Quality = null,
    long? StartPositionTicks = null);

/// <summary>One audio stream of the file, as a player offers it.</summary>
public sealed record AudioTrackView(int StreamIndex, string? Language, string? Codec, int? Channels, bool IsDefault);

/// <summary>
/// One subtitle stream of the file, as a player offers it. <see cref="CanDisplay"/> is false for an
/// image-based track (PGS, VobSub), which a browser cannot render as text; <see cref="CanBurnIn"/>
/// says whether this installation draws such a track into the video instead, when a viewer picks it.
/// </summary>
public sealed record SubtitleTrackView(
    int StreamIndex,
    string? Language,
    string? Codec,
    bool IsForced,
    bool IsDefault,
    bool IsExternal,
    bool CanDisplay,
    bool CanBurnIn = false);

/// <summary>
/// A quality the viewer may choose: the box the picture is scaled to fit and the video bitrate it is
/// held under. All three are null for <c>original</c>, the file as it is.
/// </summary>
public sealed record QualityOptionView(string Id, int? MaxWidth, int? MaxHeight, int? MaxBitrateKbps);

/// <summary>
/// What a player needs beyond the stream itself: the tracks it can switch between, the qualities on
/// offer and the one in effect, and the timeline. <see cref="StreamOffsetTicks"/> is where the stream's
/// own zero sits in the file — non-zero when a conversion started part way through — and
/// <see cref="DurationTicks"/> is the whole file's runtime when known. <see cref="BurnedInSubtitle"/>
/// is the picture subtitle this stream carries in its video, if any.
/// </summary>
public sealed record PlaybackMediaView(
    IReadOnlyList<AudioTrackView> AudioTracks,
    IReadOnlyList<SubtitleTrackView> SubtitleTracks,
    IReadOnlyList<QualityOptionView> Qualities,
    string Quality,
    long StreamOffsetTicks,
    long? DurationTicks,
    int? BurnedInSubtitle = null);

/// <summary>
/// The result of requesting playback: the session, the explainable plan, the selected tracks, and the
/// resume position. The caller (the API) turns the method into the concrete stream URL.
/// </summary>
/// <param name="ResumePositionTicks">
/// Where to start, as a single offset into the file.
/// <para>
/// Known limitation: a multi-episode file (<c>S01E01-E02</c>) is one asset
/// serving two catalog units, because <c>ux_media_versions_full_path</c> is UNIQUE on the version's
/// path and two assets over one file is schema-impossible. One offset cannot express "play episode 2
/// of this file", so <b>playback of a multi-episode file always starts at the beginning</b>. Per-episode
/// offsets are explicitly deferred; solving them requires start/end offsets on the session, which is a
/// separate increment.
/// </para>
/// </param>
/// <param name="Media">The tracks, qualities and timeline a player offers; trailing optional.</param>
public sealed record PlaybackTicket(
    PlaybackSessionId SessionId,
    PlaybackMethod Method,
    PlaybackPlanView Plan,
    StreamSelectionView Selection,
    long ResumePositionTicks,
    PlaybackMediaView? Media = null);

/// <summary>Projection of a playback session's current state.</summary>
public sealed record PlaybackSessionSummary(
    PlaybackSessionId Id,
    Guid UserId,
    Guid AssetId,
    PlaybackState State,
    PlaybackMethod Method,
    long PositionTicks,
    PlaybackEndReason? EndReason = null);

/// <summary>The full view of a session: its state, its explainable plan, and its selected tracks.</summary>
public sealed record PlaybackSessionDetail(
    PlaybackSessionSummary Session,
    PlaybackPlanView Plan,
    StreamSelectionView Selection);

/// <summary>
/// Resume state for a (user, asset) pair — powers "continue watching". <see cref="UnitId"/> is trailing
/// optional: it lets a season view map a progress row onto its episode without a second lookup, and is
/// null for rows written before the series slice. <see cref="DurationTicks"/> is the runtime the player last
/// reported (0 when none has), so a client can draw the position as a share of the whole;
/// <see cref="WorkId"/> and <see cref="UpdatedAt"/> let "continue watching" name and order a row without a
/// second lookup.
/// </summary>
public sealed record PlaybackProgressView(
    Guid AssetId,
    long PositionTicks,
    bool Played,
    int PlayCount,
    Guid? UnitId = null,
    long DurationTicks = 0,
    Guid? WorkId = null,
    DateTimeOffset? UpdatedAt = null);

/// <summary>
/// The episode a user should watch next in a series: the first one in season/episode order that has a
/// playable asset and is not yet watched, with the position to resume from.
/// <para>
/// Only meaningful for a series — a movie has no "next", so <c>GetNextUpAsync</c> returns null for one.
/// </para>
/// </summary>
public sealed record NextUpView(
    Guid WorkId,
    Guid UnitId,
    Guid AssetId,
    int SeasonNumber,
    int EpisodeNumber,
    string? EpisodeTitle,
    long ResumePositionTicks);
