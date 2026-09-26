using Cinomni.Monitoring.Application;

namespace Cinomni.Monitoring.Tests;

public sealed class CalendarWindowTests
{
    private static readonly DateOnly Today = new(2026, 9, 22);

    [Fact]
    public void An_omitted_window_is_the_next_fourteen_days()
    {
        var ok = CalendarWindow.TryCreate(null, null, Today, out var start, out var end, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(Today, start);
        Assert.Equal(Today.AddDays(13), end);
    }

    [Fact]
    public void A_window_longer_than_forty_two_days_is_refused()
    {
        var ok = CalendarWindow.TryCreate(Today, Today.AddDays(42), Today, out _, out _, out var error);

        Assert.False(ok);
        Assert.Contains("42", error);
    }

    [Theory]
    [InlineData("9999-12-25", null)]
    [InlineData("9999-12-31", null)]
    [InlineData("9999-12-20", "9999-12-31")]
    public void A_window_that_runs_off_the_end_of_the_calendar_is_refused_not_thrown(string from, string? to)
    {
        var ok = CalendarWindow.TryCreate(
            DateOnly.Parse(from), to is null ? null : DateOnly.Parse(to), Today, out _, out _, out var error);

        Assert.False(ok);
        Assert.Contains("9999-12-30", error);
    }

    [Fact]
    public void The_last_day_a_window_may_end_on_is_accepted()
    {
        var ok = CalendarWindow.TryCreate(
            new DateOnly(9999, 12, 17), null, Today, out var start, out var end, out var error);

        Assert.True(ok, error);
        Assert.Equal(new DateOnly(9999, 12, 17), start);
        Assert.Equal(CalendarWindow.LastDay, end);
    }

    [Fact]
    public void An_end_before_the_start_is_refused()
    {
        var ok = CalendarWindow.TryCreate(Today, Today.AddDays(-1), Today, out _, out _, out var error);

        Assert.False(ok);
        Assert.NotNull(error);
    }
}
