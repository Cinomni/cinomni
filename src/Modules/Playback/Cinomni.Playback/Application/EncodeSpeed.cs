namespace Cinomni.Playback.Application;

/// <summary>
/// How fast one transcode is converting, measured the way a viewer experiences it: seconds of video
/// written per second of wall clock. Below real time the playlist falls further behind the player with
/// every segment, and the player buffers for ever without anything failing — so a stream that stays
/// below <see cref="MinimumSpeed"/> for a whole <see cref="Window"/> is ended and explained instead.
/// <para>
/// Pure: the sweep hands it the clock and the playlist reading, so a test can drive it. The first
/// <see cref="WarmUp"/> is ignored, because opening the file, seeking and starting the encoder are slow
/// once and say nothing about how fast the rest will go.
/// </para>
/// </summary>
public sealed class EncodeSpeed(DateTimeOffset attachedAt)
{
    /// <summary>Below this a stream cannot keep up; the margin under 1× absorbs a heavy scene.</summary>
    public const double MinimumSpeed = 0.9;

    public static readonly TimeSpan WarmUp = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long one measurement spans. About two sweeps: short enough that a viewer is told within a
    /// couple of minutes, long enough that one heavy scene does not end a stream that is fine on average.
    /// </summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    private DateTimeOffset? _baselineAt;
    private double _baselineSeconds;

    /// <summary>
    /// Records a reading of how much video has been written. Returns the speed over the window that
    /// just closed, or null while warming up or until a full window has passed since the last one.
    /// </summary>
    public double? Observe(DateTimeOffset now, double encodedSeconds)
    {
        if (now - attachedAt < WarmUp)
        {
            return null;
        }

        if (_baselineAt is not { } since)
        {
            _baselineAt = now;
            _baselineSeconds = encodedSeconds;
            return null;
        }

        var elapsed = (now - since).TotalSeconds;
        if (elapsed < Window.TotalSeconds)
        {
            return null;
        }

        var speed = Math.Max(0, encodedSeconds - _baselineSeconds) / elapsed;
        _baselineAt = now;
        _baselineSeconds = encodedSeconds;
        return speed;
    }
}
