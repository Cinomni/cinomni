using Cinomni.Playback.Contracts;
using Cinomni.Playback.Persistence;
using Cinomni.Playback.Planning;

namespace Cinomni.Playback.Tests;

/// <summary>Pure unit tests for the PlaybackSession and PlaybackProgress finite-state machines (no database).</summary>
public sealed class PlaybackSessionFsmTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private const long Duration = 1000;

    private static PlaybackSession NewSession(PlaybackMethod method = PlaybackMethod.DirectPlay, long resume = 0)
    {
        var plan = new PlaybackPlan(method, [], [], EncoderBackend.Software, [], DecodeAccelerated: false);
        return PlaybackSession.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "/lib/movie.mkv", "matroska",
            plan, audioStreamIndex: 1, subtitleStreamIndex: null, resumePositionTicks: resume, playSessionId: null, Now);
    }

    [Fact]
    public void Create_starts_in_starting_at_the_resume_position()
    {
        var session = NewSession(resume: 250);

        Assert.Equal(PlaybackState.Starting, session.State);
        Assert.Equal(250, session.PositionTicks);
    }

    [Fact]
    public void Begin_moves_to_the_delivery_state_of_the_method()
    {
        var direct = NewSession(PlaybackMethod.DirectPlay);
        direct.Begin(Now);
        Assert.Equal(PlaybackState.DirectPlaying, direct.State);

        var transcode = NewSession(PlaybackMethod.Transcode);
        transcode.Begin(Now);
        Assert.Equal(PlaybackState.Transcoding, transcode.State);
    }

    [Fact]
    public void Progress_pauses_and_resumes()
    {
        var session = NewSession();
        session.Begin(Now);

        session.ReportProgress(300, Duration, isPaused: true, Now);
        Assert.Equal(PlaybackState.Paused, session.State);

        session.ReportProgress(300, Duration, isPaused: false, Now);
        Assert.Equal(PlaybackState.DirectPlaying, session.State);
    }

    [Fact]
    public void Progress_past_the_threshold_completes_the_session_once()
    {
        var session = NewSession();
        session.Begin(Now);

        Assert.True(session.ReportProgress(950, Duration, isPaused: false, Now));
        Assert.Equal(PlaybackState.Completed, session.State);

        // A further report on a completed session is a no-op.
        Assert.False(session.ReportProgress(960, Duration, isPaused: false, Now));
    }

    [Fact]
    public void Stop_completes_an_active_session_and_is_idempotent()
    {
        var session = NewSession();
        session.Begin(Now);

        Assert.True(session.Stop(Now));
        Assert.Equal(PlaybackState.Completed, session.State);
        Assert.False(session.Stop(Now));
    }

    [Fact]
    public void Fail_moves_to_failed()
    {
        var session = NewSession(PlaybackMethod.Transcode);
        session.Begin(Now);

        session.Fail(Now);
        Assert.Equal(PlaybackState.Failed, session.State);
    }

    [Fact]
    public void Progress_before_begin_is_a_no_op()
    {
        var session = NewSession(); // still Starting

        Assert.False(session.ReportProgress(500, Duration, isPaused: false, Now));
        Assert.Equal(PlaybackState.Starting, session.State);
    }

    [Fact]
    public void Progress_records_a_monotonic_sequence()
    {
        var session = NewSession();
        session.Begin(Now);

        session.ReportProgress(100, Duration, isPaused: false, Now);
        session.ReportProgress(200, Duration, isPaused: false, Now);

        Assert.Equal(2, session.ProgressSeq);
    }
}
