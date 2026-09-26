using Cinomni.Subtitles.Application;

namespace Cinomni.Subtitles.Tests;

public sealed class SubtitleRetryTests
{
    private static readonly DateTimeOffset FailedAt = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_first_miss_waits_six_hours_and_a_fifth_attempt_stops()
    {
        Assert.False(SubtitleRetry.IsDue(1, FailedAt, FailedAt.AddHours(5)));
        Assert.True(SubtitleRetry.IsDue(1, FailedAt, FailedAt.AddHours(6)));
        Assert.False(SubtitleRetry.IsDue(SubtitleRetry.MaxAttempts, FailedAt, FailedAt.AddDays(30)));
    }
}
