namespace Cinomni.Subtitles.Application;

/// <summary>
/// Process-wide pacer for outbound subtitle-provider searches. Registered as a <b>singleton</b>: the
/// burst it exists to flatten spans scopes, because a season pack registers N assets in one import and
/// every one of them fires its own <c>search-subtitles:{assetId}</c> command.
/// <para>
/// It has two halves, and which one you use decides who pays for the pacing:
/// </para>
/// <list type="bullet">
/// <item>
/// <see cref="Reserve"/> is the <b>schedule</b>. A producer that knows it is about to fan out books its
/// slots up front and defers each command to its own instant (<c>ICommandQueue.EnqueueAtAsync</c>), so
/// the burst is spread over the wall clock. Nothing waits: the platform runs ONE command worker and
/// <c>CommandProcessor.ProcessBatchAsync</c> dispatches its batch strictly sequentially, so a handler
/// that sleeps for an interval stalls every other module's commands behind it.
/// </item>
/// <item>
/// <see cref="RunAsync"/> is the <b>gate</b>, and it stays: a caller that arrives early anyway — a
/// second wanted language inside one command, a worker catching up after a stall, a direct call —
/// must still not let N searches reach the provider faster than one interval apart. In the scheduled
/// path its wait is already spent, so the gate costs nothing.
/// </item>
/// </list>
/// <para>
/// The gate is deliberately held across the provider call, not merely across the wait. A rate-limited
/// API counts concurrent requests too, so letting N calls start one interval apart but overlap would
/// only reshape the burst. One call at a time, at least
/// <see cref="SubtitleOptions.ProviderCallInterval"/> apart, is the whole contract.
/// </para>
/// <para>
/// Every instant it reads and every wait it charges go through <see cref="TimeProvider"/>. In
/// production that is <see cref="TimeProvider.System"/>, so the behaviour is the monotonic stopwatch
/// and the real delay it always was; a test can supply a virtual clock and then observe exactly how
/// much stagger was charged instead of timing the work with its own wall clock.
/// </para>
/// </summary>
public sealed class SubtitleProviderThrottle(SubtitleOptions options, TimeProvider timeProvider) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _scheduleLock = new();

    /// <summary>Monotonic <see cref="TimeProvider.GetTimestamp"/> of the earliest allowed next call.</summary>
    private long _nextAllowedStamp;

    /// <summary>Wall-clock instant of the earliest unreserved slot (see <see cref="Reserve"/>).</summary>
    private DateTimeOffset _nextFreeSlot = DateTimeOffset.MinValue;

    /// <summary>
    /// Books <paramref name="calls"/> consecutive slots on the outbound schedule and returns the first
    /// of them — the instant from which the caller may make its provider calls. The caller is expected
    /// to defer its work to that instant rather than wait for it here. Returns <c>null</c> when there is
    /// nothing to pace: no interval configured, or no call to make.
    /// </summary>
    public DateTimeOffset? Reserve(int calls)
    {
        var interval = options.ProviderCallInterval;
        if (interval <= TimeSpan.Zero || calls <= 0)
        {
            return null;
        }

        var now = timeProvider.GetUtcNow();
        lock (_scheduleLock)
        {
            // A schedule that has fallen behind restarts from now: slots are a floor on when a call may
            // go out, never a backlog to be worked off.
            var slot = _nextFreeSlot > now ? _nextFreeSlot : now;
            _nextFreeSlot = slot + (interval * calls);
            return slot;
        }
    }

    /// <summary>
    /// Runs <paramref name="call"/> as the only in-flight provider call, no sooner than one interval
    /// after the previous one was released. Cancellation is observed while waiting for the turn.
    /// </summary>
    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> call, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await WaitForTurnAsync(cancellationToken);
            return await call(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private async Task WaitForTurnAsync(CancellationToken cancellationToken)
    {
        var interval = options.ProviderCallInterval;
        if (interval <= TimeSpan.Zero)
        {
            return;
        }

        // The monotonic timestamp rather than the wall clock: the stagger must survive a clock
        // adjustment, and its resolution has to be finer than the interval a test injects.
        var now = timeProvider.GetTimestamp();
        var wait = timeProvider.GetElapsedTime(now, _nextAllowedStamp);
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, timeProvider, cancellationToken);

            // Advance on the ideal grid, not on the overshoot, so a slow timer tick cannot compound.
            now = _nextAllowedStamp;
        }

        _nextAllowedStamp = now + (long)(interval.TotalSeconds * timeProvider.TimestampFrequency);
    }
}
