using Cinomni.Subtitles.Application;

namespace Cinomni.Subtitles.Tests;

/// <summary>
/// The pacing contract itself, with no database and no wall clock: the gate does hold calls one
/// interval apart, and the schedule does hand out consecutive slots. The flow tests prove where that
/// cost is paid; these prove it is still charged at all, which is the half a determinism fix could
/// quietly delete.
/// </summary>
public sealed class SubtitleThrottlePacingTests
{
    private static readonly DateTimeOffset Start = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    private readonly VirtualClock _clock = new(Start);

    [Fact]
    public async Task The_first_call_goes_straight_out_and_every_later_one_waits_its_interval()
    {
        using var throttle = Throttle(Interval);
        var calls = new List<DateTimeOffset>();

        for (var i = 0; i < 3; i++)
        {
            await throttle.RunAsync(_ =>
            {
                calls.Add(_clock.GetUtcNow());
                return Task.FromResult(0);
            });
        }

        // No call may reach a rate-limited provider sooner than one interval after the previous one.
        Assert.Equal([Start, Start + Interval, Start + (Interval * 2)], calls);
        Assert.Equal(Interval * 2, _clock.Charged);
    }

    [Fact]
    public async Task An_interval_of_zero_turns_the_throttle_off_entirely()
    {
        using var throttle = Throttle(TimeSpan.Zero);

        Assert.Null(throttle.Reserve(3));
        for (var i = 0; i < 3; i++)
        {
            await throttle.RunAsync(_ => Task.FromResult(0));
        }

        Assert.Equal(TimeSpan.Zero, _clock.Charged);
    }

    [Fact]
    public void A_reservation_books_consecutive_slots_and_nothing_waits_for_them()
    {
        using var throttle = Throttle(Interval);

        // Two calls booked, so the next caller starts after both of them.
        Assert.Equal(Start, throttle.Reserve(2));
        Assert.Equal(Start + (Interval * 2), throttle.Reserve(1));

        // Booking a slot is not waiting for it: that is the whole reason the command worker stays free.
        Assert.Equal(TimeSpan.Zero, _clock.Charged);
        Assert.Equal(Start, _clock.GetUtcNow());
    }

    [Fact]
    public void A_schedule_the_world_has_overtaken_restarts_from_now()
    {
        using var throttle = Throttle(Interval);
        throttle.Reserve(2);

        // Slots are a floor on when a call may go out, never a backlog to be worked off: an installation
        // that was idle for an hour must not fire its next searches an hour ago.
        _clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(_clock.GetUtcNow(), throttle.Reserve(1));
    }

    [Fact]
    public async Task Nothing_is_paced_when_there_is_nothing_to_pace()
    {
        using var throttle = Throttle(Interval);

        Assert.Null(throttle.Reserve(0));
        Assert.Null(throttle.Reserve(-1));

        // ...and the calls that follow are still the first ones through the gate.
        await throttle.RunAsync(_ => Task.FromResult(0));
        Assert.Equal(TimeSpan.Zero, _clock.Charged);
    }

    private SubtitleProviderThrottle Throttle(TimeSpan interval) =>
        new(new SubtitleOptions { ProviderCallInterval = interval }, _clock);
}
