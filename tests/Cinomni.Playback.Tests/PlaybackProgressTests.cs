using Cinomni.Playback.Persistence;

namespace Cinomni.Playback.Tests;

/// <summary>Pure unit tests for the per-(user,asset) resume progress thresholds.</summary>
public sealed class PlaybackProgressTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private const long Duration = 1000;

    private static PlaybackProgress New() => PlaybackProgress.Create(Guid.NewGuid(), Guid.NewGuid(), Now);

    [Fact]
    public void Below_the_floor_resets_to_the_start()
    {
        var progress = New();

        progress.Record(30, Duration, audioStreamIndex: 1, subtitleStreamIndex: null, Now); // 3% < 5%

        Assert.Equal(0, progress.PositionTicks);
        Assert.False(progress.Played);
    }

    [Fact]
    public void In_the_middle_stores_the_position()
    {
        var progress = New();

        progress.Record(400, Duration, 1, null, Now); // 40%

        Assert.Equal(400, progress.PositionTicks);
        Assert.False(progress.Played);
    }

    [Fact]
    public void Past_the_threshold_marks_watched()
    {
        var progress = New();

        progress.Record(950, Duration, 1, null, Now); // 95% > 90%

        Assert.True(progress.Played);
    }

    [Fact]
    public void CountPlay_increments_and_marks_watched()
    {
        var progress = New();

        progress.CountPlay(Now);
        progress.CountPlay(Now);

        Assert.Equal(2, progress.PlayCount);
        Assert.True(progress.Played);
    }

    [Fact]
    public void Record_stores_the_reported_duration()
    {
        var progress = New();

        progress.Record(400, Duration, 1, null, Now);

        Assert.Equal(Duration, progress.DurationTicks);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void A_report_without_a_duration_keeps_the_last_known_one(long reported)
    {
        var progress = New();
        progress.Record(400, Duration, 1, null, Now);

        progress.Record(500, reported, 1, null, Now);

        Assert.Equal(Duration, progress.DurationTicks);
    }

    [Fact]
    public void Record_keeps_the_selected_tracks()
    {
        var progress = New();

        progress.Record(400, Duration, audioStreamIndex: 2, subtitleStreamIndex: 3, Now);

        Assert.Equal(2, progress.AudioStreamIndex);
        Assert.Equal(3, progress.SubtitleStreamIndex);
    }
}
