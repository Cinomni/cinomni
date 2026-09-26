using Cinomni.Library.Contracts;
using Cinomni.Playback.Contracts;

namespace Cinomni.Playback.Application;

/// <summary>
/// The tracks of a file as a player offers them, and which of them the session plays. Pure: the
/// streams come from Library, the choice from the viewer or from what they chose last time.
/// </summary>
public static class PlaybackTracks
{
    /// <summary>Subtitle codecs that are text, which a browser can show once converted to WebVTT.</summary>
    private static readonly HashSet<string> TextSubtitleCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "subrip", "srt", "ass", "ssa", "webvtt", "mov_text", "text",
    };

    /// <summary>
    /// Whether a subtitle stream can be shown as text. A sidecar Subtitles fetched always is (SubRip, ASS
    /// or WebVTT); an embedded one is when its codec is text — PGS and VobSub are pictures.
    /// </summary>
    public static bool CanDisplay(MediaStreamSummary stream) =>
        stream.Type == MediaStreamType.Subtitle
        && (stream.IsExternal || (stream.Codec is { } codec && TextSubtitleCodecs.Contains(codec)));

    public static IReadOnlyList<AudioTrackView> Audio(MediaVersionSummary version) =>
        version.Streams
            .Where(s => s.Type == MediaStreamType.Audio)
            .Select(s => new AudioTrackView(s.StreamIndex, s.Language, s.Codec, s.Channels, s.IsDefault))
            .ToList();

    /// <param name="canBurnIn">Whether this installation burns a picture subtitle in when a viewer picks it.</param>
    public static IReadOnlyList<SubtitleTrackView> Subtitles(MediaVersionSummary version, bool canBurnIn = false) =>
        version.Streams
            .Where(s => s.Type == MediaStreamType.Subtitle)
            .Select(s =>
            {
                var text = CanDisplay(s);
                return new SubtitleTrackView(
                    s.StreamIndex, s.Language, s.Codec, s.IsForced, s.IsDefault, s.IsExternal, text,
                    CanBurnIn: !text && canBurnIn);
            })
            .ToList();

    /// <summary>The audio stream a player picks from the file by itself: the one flagged default, else the first.</summary>
    public static int? DefaultAudio(MediaVersionSummary version)
    {
        var audio = version.Streams.Where(s => s.Type == MediaStreamType.Audio).ToList();
        return (audio.FirstOrDefault(s => s.IsDefault) ?? audio.FirstOrDefault())?.StreamIndex;
    }

    /// <summary>
    /// The audio stream to play: the viewer's explicit choice, else their last choice for this title while
    /// the file still has it, else the file's default. False when an explicit choice names no audio stream.
    /// </summary>
    public static bool TrySelectAudio(MediaVersionSummary version, int? requested, int? remembered, out int? selected)
    {
        var audio = version.Streams.Where(s => s.Type == MediaStreamType.Audio).Select(s => s.StreamIndex).ToList();
        if (requested is { } index)
        {
            selected = index;
            return audio.Contains(index);
        }

        selected = remembered is { } last && audio.Contains(last) ? last : DefaultAudio(version);
        return true;
    }

    /// <summary>
    /// The subtitle stream shown: none when the viewer turned them off, the viewer's explicit choice, else
    /// their last choice for this title, else a forced or default track of the file. False when an explicit
    /// choice names no subtitle stream.
    /// </summary>
    public static bool TrySelectSubtitle(
        MediaVersionSummary version, PlaybackPreferences? preferences, int? remembered, out int? selected)
    {
        var subtitles = version.Streams.Where(s => s.Type == MediaStreamType.Subtitle).ToList();
        if (preferences?.SubtitlesOff == true)
        {
            selected = null;
            return true;
        }

        if (preferences?.SubtitleStreamIndex is { } index)
        {
            selected = index;
            return subtitles.Any(s => s.StreamIndex == index);
        }

        selected = remembered is { } last && subtitles.Any(s => s.StreamIndex == last)
            ? last
            : subtitles.FirstOrDefault(s => s.IsForced || s.IsDefault)?.StreamIndex;
        return true;
    }
}
