using Cinomni.Playback.Contracts;
using Cinomni.Playback.Persistence;
using Cinomni.Playback.Planning;

namespace Cinomni.Playback.Tests;

/// <summary>
/// What a session counts as the film's runtime, and therefore when it is watched. Found on a real 4K
/// file converted below real time: the player reported the converted stream's length — the minute that
/// existed so far — as the runtime, caught up with it, and the film was marked watched a minute in.
/// </summary>
public sealed class TrustedRuntimeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly long Minute = TimeSpan.FromMinutes(1).Ticks;
    private static readonly long Film = TimeSpan.FromMinutes(114).Ticks;

    [Fact]
    public void A_converted_stream_with_no_known_runtime_is_never_watched_by_catching_up_with_the_conversion()
    {
        var session = Open(PlaybackMethod.Transcode, runtimeTicks: 0);

        // 56 s into the 60 s converted so far: 93% of what the player sees.
        var completed = session.ReportProgress(56 * Minute / 60, Minute, isPaused: false, Now);

        Assert.False(completed);
        Assert.False(session.HasEnded);
        Assert.Equal(0, session.DurationTicks);
    }

    [Fact]
    public void The_servers_runtime_decides_and_a_report_cannot_replace_it()
    {
        var session = Open(PlaybackMethod.Transcode, runtimeTicks: Film);

        Assert.False(session.ReportProgress(56 * Minute / 60, Minute, isPaused: false, Now));
        Assert.Equal(Film, session.DurationTicks);

        Assert.True(session.ReportProgress(Film * 95 / 100, Minute, isPaused: false, Now));
        Assert.Equal(PlaybackEndReason.Watched, session.EndReason);
    }

    [Theory]
    [InlineData(PlaybackMethod.Transcode)]
    [InlineData(PlaybackMethod.Remux)]
    public void Only_direct_play_may_supply_a_runtime_the_server_does_not_have(PlaybackMethod method)
    {
        var converted = Open(method, runtimeTicks: 0);
        var direct = Open(PlaybackMethod.DirectPlay, runtimeTicks: 0);

        converted.ReportProgress(10 * Minute, Film, isPaused: false, Now);
        direct.ReportProgress(10 * Minute, Film, isPaused: false, Now);

        Assert.Equal(0, converted.DurationTicks);
        Assert.Equal(Film, direct.DurationTicks);
    }

    [Fact]
    public void Progress_without_a_runtime_keeps_the_position_and_marks_nothing_played()
    {
        var progress = PlaybackProgress.Create(Guid.NewGuid(), Guid.NewGuid(), Now);

        progress.Record(56 * Minute / 60, durationTicks: 0, audioStreamIndex: 1, subtitleStreamIndex: null, Now);

        Assert.Equal(56 * Minute / 60, progress.PositionTicks);
        Assert.False(progress.Played);
        Assert.Equal(0, progress.DurationTicks);
    }

    private static PlaybackSession Open(PlaybackMethod method, long runtimeTicks)
    {
        var plan = new PlaybackPlan(method, [], [], EncoderBackend.Software, [], DecodeAccelerated: false);
        var session = PlaybackSession.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "/lib/film.mkv", "matroska",
            plan, audioStreamIndex: 1, subtitleStreamIndex: null, resumePositionTicks: 0, playSessionId: null, Now,
            runtimeTicks);
        session.Begin(Now);
        return session;
    }
}
