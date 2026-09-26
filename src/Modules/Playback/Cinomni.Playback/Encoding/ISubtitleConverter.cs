namespace Cinomni.Playback.Encoding;

/// <summary>
/// Port to FFmpeg for turning subtitles a browser cannot read into WebVTT, which it can. The
/// production adapter (<see cref="FfmpegSubtitleConverter"/>) runs FFmpeg with an argv list, bounded in
/// time and in output; tests substitute a fake. Either method answers null when the conversion fails —
/// a subtitle that cannot be shown is not a playback failure.
/// </summary>
public interface ISubtitleConverter
{
    /// <summary>One subtitle stream of a media file (by its absolute stream index), as WebVTT.</summary>
    Task<string?> ExtractToWebVttAsync(string mediaPath, int streamIndex, CancellationToken cancellationToken = default);

    /// <summary>The text of an ASS/SSA file, as WebVTT — styling dropped, the timing and the words kept.</summary>
    Task<string?> AssToWebVttAsync(string assText, CancellationToken cancellationToken = default);
}
