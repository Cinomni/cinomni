namespace Cinomni.Subtitles.Application;

/// <summary>
/// When a search that found nothing may ask again. The wait grows with the attempt count and then
/// stops, so a title nobody has a subtitle for is not asked about forever.
/// </summary>
public static class SubtitleRetry
{
    public const int MaxAttempts = 5;

    public static TimeSpan? Delay(int attempts) => attempts switch
    {
        <= 1 => TimeSpan.FromHours(6),
        2 => TimeSpan.FromDays(1),
        3 => TimeSpan.FromDays(3),
        4 => TimeSpan.FromDays(7),
        _ => null,
    };

    public static bool IsDue(int attempts, DateTimeOffset updatedAt, DateTimeOffset now)
    {
        var delay = Delay(attempts);
        return delay is { } wait && updatedAt + wait <= now;
    }
}
