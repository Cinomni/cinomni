using Cinomni.Library.Contracts;
using Cinomni.Playback.Contracts;

namespace Cinomni.Playback.Planning;

/// <summary>
/// What the planner needs to know about the asset: its container and the streams' codecs/geometry.
/// <see cref="AudioCodecs"/> are the codecs of the audio that will actually play — the selected track.
/// <see cref="SelectedAudioIsDefault"/> says whether that track is the one a player picks from the file
/// by itself, which is the only one a file played as-is can offer. <see cref="VideoWidth"/> and
/// <see cref="Bitrate"/> (bits per second) are null when unknown.
/// <para>
/// <see cref="VideoRange"/> is the picture's dynamic range (null when unknown, read as SDR),
/// <see cref="AudioChannels"/> the selected track's channel count, and
/// <see cref="ImageSubtitleIndex"/> the stream index of a picture subtitle (PGS, VobSub) the viewer
/// chose — one the browser cannot draw, so it can only be seen burned into the video.
/// </para>
/// </summary>
public sealed record PlaybackSource(
    string Container,
    string? VideoCodec,
    int? VideoHeight,
    IReadOnlyList<string> AudioCodecs,
    int? VideoWidth = null,
    long? Bitrate = null,
    bool SelectedAudioIsDefault = true,
    VideoRangeType? VideoRange = null,
    int? AudioChannels = null,
    int? ImageSubtitleIndex = null)
{
    public bool IsHdr => VideoRange is VideoRangeType.Hdr10 or VideoRangeType.Hdr10Plus or VideoRangeType.Hlg or VideoRangeType.DoVi;
}

/// <summary>
/// Decides how to deliver an asset to a client and <b>explains why</b>. A pure
/// function of the asset's streams, the client's capabilities, the viewer's quality, the operator's
/// transcoding settings and the host's detected hardware — no FFmpeg, so it is unit-tested by the
/// decisions it produces, not by running a transcode.
/// </summary>
public interface IPlaybackPlanner
{
    /// <param name="quality">The ceiling the viewer chose, or null for the file as it is.</param>
    PlaybackPlan Plan(PlaybackSource source, ClientCapability capability, QualityStep? quality = null);
}
