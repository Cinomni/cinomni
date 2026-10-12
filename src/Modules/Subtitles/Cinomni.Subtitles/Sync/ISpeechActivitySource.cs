namespace Cinomni.Subtitles.Sync;

/// <summary>
/// How speech-like each 10 ms of one stretch of a video's audio is (<see cref="SpeechDetector.Activity"/>),
/// or why it could not be read. A failure is an
/// expected outcome (FFmpeg missing, too slow, an unreadable file), not an exception: the pass records
/// it and keeps the original subtitle.
/// </summary>
public sealed record SpeechActivity(double[]? Speech, string? Failure)
{
    public static SpeechActivity Found(double[] speech) => new(speech, null);

    public static SpeechActivity Failed(string reason) => new(null, reason);
}

/// <summary>
/// Port for reading speech activity out of a video's audio. The production adapter runs FFmpeg
/// (<see cref="FfmpegSpeechActivitySource"/>); tests substitute a fake that returns generated speech.
/// </summary>
public interface ISpeechActivitySource
{
    /// <summary>
    /// Speech activity on the 10 ms grid for <paramref name="length"/> of audio stream
    /// <paramref name="audioStreamIndex"/> from <paramref name="start"/>.
    /// </summary>
    Task<SpeechActivity> DetectAsync(
        string mediaPath,
        int audioStreamIndex,
        TimeSpan start,
        TimeSpan length,
        CancellationToken cancellationToken = default);
}
