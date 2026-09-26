namespace Cinomni.Monitoring.Application;

/// <summary>
/// The date window a calendar page may ask for. Bounded so a caller cannot turn a read of upcoming
/// airings into a scan of every target the installation has ever materialised.
/// </summary>
public static class CalendarWindow
{
    /// <summary>Inclusive days a caller may ask for in one request.</summary>
    public const int MaxSpanDays = 42;

    /// <summary>Inclusive days returned when the caller names neither end.</summary>
    public const int DefaultSpanDays = 14;

    /// <summary>
    /// The last day a window may end on. The read turns the end into an exclusive instant a day later,
    /// so the very last representable date cannot be one.
    /// </summary>
    public static readonly DateOnly LastDay = DateOnly.MaxValue.AddDays(-1);

    public static bool TryCreate(
        DateOnly? from,
        DateOnly? to,
        DateOnly today,
        out DateOnly start,
        out DateOnly end,
        out string? error)
    {
        start = from ?? today;
        end = default;
        error = null;

        // Day numbers, not AddDays: a start near the end of the calendar would overflow DateOnly while
        // working out the default end, and answer 500 instead of saying the window is out of range.
        var endDay = to?.DayNumber ?? start.DayNumber + DefaultSpanDays - 1;
        if (endDay > LastDay.DayNumber)
        {
            error = $"A calendar window must end by {LastDay:yyyy-MM-dd}.";
            return false;
        }

        end = DateOnly.FromDayNumber(endDay);
        if (end < start)
        {
            error = "The end date is before the start date.";
            return false;
        }

        var inclusive = end.DayNumber - start.DayNumber + 1;
        if (inclusive > MaxSpanDays)
        {
            error = $"A calendar window may cover at most {MaxSpanDays} days.";
            return false;
        }

        return true;
    }
}
