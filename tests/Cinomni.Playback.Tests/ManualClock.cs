namespace Cinomni.Playback.Tests;

/// <summary>
/// A <see cref="TimeProvider"/> that only moves when a test moves it, so "nothing asked for this
/// stream in five minutes" is a line of the test rather than five minutes of waiting.
/// </summary>
internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private readonly Lock _gate = new();
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public void Advance(TimeSpan by)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(by, TimeSpan.Zero);
        lock (_gate)
        {
            _now += by;
        }
    }
}
