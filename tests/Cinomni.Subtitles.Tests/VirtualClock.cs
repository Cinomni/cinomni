namespace Cinomni.Subtitles.Tests;

/// <summary>
/// A <see cref="TimeProvider"/> the test moves by hand, so a schedule can be asserted instead of
/// timed. Nothing here ever sleeps: a delay the code under test asks for is granted immediately, and
/// the clock jumps to its deadline. That is the point — the wait becomes an observable quantity
/// (<see cref="Charged"/>) rather than time a loaded CI runner also spends on its own work.
/// <para>
/// Hand-written rather than taken from a testing package: this needs a delay that completes on the
/// spot. A fake that only completes when the test advances the clock would turn "the code waited when
/// it should not have" into a hang, and a test that hangs is not a test that fails.
/// </para>
/// </summary>
internal sealed class VirtualClock(DateTimeOffset start) : TimeProvider
{
    private readonly Lock _lock = new();
    private DateTimeOffset _now = start;
    private TimeSpan _charged;

    /// <summary>
    /// Total time the code under test has waited out, i.e. the stagger it charged. Time the test
    /// itself skips with <see cref="Advance"/> is not charged to anyone.
    /// </summary>
    public TimeSpan Charged
    {
        get
        {
            lock (_lock)
            {
                return _charged;
            }
        }
    }

    /// <summary>Ticks per second, so <see cref="GetTimestamp"/> can simply be the current instant.</summary>
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_lock)
        {
            return _now;
        }
    }

    /// <summary>
    /// The monotonic half of the same clock. Overridden because the base implementation would hand back
    /// the machine's real stopwatch and quietly reintroduce wall-clock time.
    /// </summary>
    public override long GetTimestamp()
    {
        lock (_lock)
        {
            return _now.UtcTicks;
        }
    }

    /// <summary>Moves the world forward, the way waiting would have, without waiting.</summary>
    public void Advance(TimeSpan by)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(by, TimeSpan.Zero);
        lock (_lock)
        {
            _now += by;
        }
    }

    /// <summary>
    /// The one timer shape this clock serves: the single-shot timer behind
    /// <c>Task.Delay(delay, timeProvider, token)</c>. Firing the callback here, before the delay's task
    /// is handed back, is what makes the wait cost nothing in real time.
    /// </summary>
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (period != Timeout.InfiniteTimeSpan)
        {
            throw new NotSupportedException("VirtualClock only serves single-shot timers.");
        }

        if (dueTime > TimeSpan.Zero)
        {
            lock (_lock)
            {
                _now += dueTime;
                _charged += dueTime;
            }
        }

        var timer = new ImmediateTimer();
        callback(state);
        return timer;
    }

    /// <summary>Already fired by the time it exists; changing or disposing it is a no-op.</summary>
    private sealed class ImmediateTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => false;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
