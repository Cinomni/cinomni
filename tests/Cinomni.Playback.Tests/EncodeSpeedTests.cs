using Cinomni.Playback.Application;
using Cinomni.Playback.Contracts;
using Cinomni.Playback.Persistence;
using Cinomni.Playback.Planning;

namespace Cinomni.Playback.Tests;

/// <summary>
/// How a stream that cannot keep up is told apart from one that can, and what the viewer is told when
/// it cannot. A 4K source converted on the CPU used to buffer for ever without anything failing; these
/// pin the measurement that ends it and the words that explain it.
/// </summary>
public sealed class EncodeSpeedTests
{
    private static readonly DateTimeOffset Attached = new(2026, 10, 11, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Nothing_is_concluded_while_the_encoder_warms_up()
    {
        var speed = new EncodeSpeed(Attached);

        Assert.Null(speed.Observe(Attached + TimeSpan.FromSeconds(10), 0));
        Assert.Null(speed.Observe(Attached + TimeSpan.FromSeconds(29), 6));
    }

    [Fact]
    public void A_speed_is_reported_once_a_whole_window_has_passed_since_the_first_reading()
    {
        var speed = new EncodeSpeed(Attached);
        var start = Attached + EncodeSpeed.WarmUp;

        Assert.Null(speed.Observe(start, 12));
        Assert.Null(speed.Observe(start + TimeSpan.FromSeconds(30), 30));
        var measured = speed.Observe(start + TimeSpan.FromSeconds(60), 48);

        Assert.NotNull(measured);
        Assert.Equal(0.6, measured.Value, precision: 3);
    }

    [Fact]
    public void Each_window_is_measured_from_the_end_of_the_last_one()
    {
        var speed = new EncodeSpeed(Attached);
        var start = Attached + EncodeSpeed.WarmUp;
        speed.Observe(start, 0);
        speed.Observe(start + TimeSpan.FromSeconds(60), 30);

        // A slow first minute does not drag down a second one that keeps up.
        var second = speed.Observe(start + TimeSpan.FromSeconds(120), 96);

        Assert.Equal(1.1, second!.Value, precision: 3);
    }

    [Fact]
    public void A_playlist_that_shrank_reads_as_no_progress_rather_than_negative_speed()
    {
        var speed = new EncodeSpeed(Attached);
        var start = Attached + EncodeSpeed.WarmUp;
        speed.Observe(start, 60);

        Assert.Equal(0, speed.Observe(start + TimeSpan.FromSeconds(60), 30));
    }

    [Fact]
    public void The_playlist_measures_the_segments_it_lists_and_skips_lines_that_are_not_durations()
    {
        const string playlist =
            "#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXTINF:6.000000,\nseg_000.ts\n#EXTINF:5.5,\nseg_001.ts\n"
            + "#EXTINF:garbage,\nseg_002.ts\n#EXTINF:-3,\n#EXTINF:NaN,\n#EXTINF:4\r\nseg_003.ts\r\n";

        Assert.Equal(15.5, HlsPlaylist.EncodedSeconds(playlist), precision: 6);
    }

    [Fact]
    public void A_job_killed_by_the_system_says_so_and_ends_its_session_as_out_of_memory()
    {
        var session = RunningTranscode();

        var failed = session.RecordTranscodeExit(137, string.Empty, Attached);

        Assert.Equal(PlaybackState.Failed, session.State);
        Assert.Equal(PlaybackEndReason.ResourceKilled, session.EndReason);
        Assert.Contains("ran out of memory", Assert.Single(failed!).LastError, StringComparison.Ordinal);
        Assert.Equal(PlaybackFailureExplainer.ResourceKilledCode, PlaybackFailureExplainer.Explain(session)!.Code);
    }

    [Fact]
    public void Any_other_failed_exit_keeps_the_plain_reason_and_adds_no_explanation()
    {
        var session = RunningTranscode();

        session.RecordTranscodeExit(1, "Invalid data found", Attached);

        Assert.Equal(PlaybackEndReason.TranscodeFailed, session.EndReason);
        Assert.Null(PlaybackFailureExplainer.Explain(session));
    }

    [Fact]
    public void A_stream_too_slow_on_the_cpu_names_the_source_and_what_an_administrator_can_change()
    {
        var session = RunningTranscode(
            EncoderBackend.Software,
            new PlaybackDecision("videoHeight", "1080", "2160", "fail"),
            new PlaybackDecision("dynamicRange", "SDR", "Hdr10", "ok"));

        var failed = session.FailTooSlow("Converted at 0.62x real time.", Attached);
        var failure = PlaybackFailureExplainer.Explain(session);

        Assert.Equal(PlaybackEndReason.TooSlow, session.EndReason);
        Assert.Equal("Converted at 0.62x real time.", Assert.Single(failed!).LastError);
        Assert.Equal(PlaybackFailureExplainer.TooSlowCode, failure!.Code);
        Assert.Contains("4K HDR video on its CPU", failure.Message, StringComparison.Ordinal);
        Assert.Contains("CINOMNI_CPUS", failure.Message, StringComparison.Ordinal);
        // What FFmpeg or the job recorded never reaches the viewer's words.
        Assert.DoesNotContain("0.62", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_hardware_stream_that_fell_back_to_the_cpu_is_explained_as_the_cpu()
    {
        var session = RunningTranscode(EncoderBackend.Vaapi);
        session.TranscodeJobs[0].RecordFallback("hardware exited before a manifest");

        session.FailTooSlow("slow", Attached);

        Assert.Contains("on its CPU", PlaybackFailureExplainer.Explain(session)!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_session_that_already_ended_keeps_its_reason_when_it_is_found_too_slow()
    {
        var session = RunningTranscode();
        session.Stop(Attached);

        Assert.Null(session.FailTooSlow("slow", Attached));
        Assert.Equal(PlaybackEndReason.Stopped, session.EndReason);
    }

    private static PlaybackSession RunningTranscode(
        EncoderBackend backend = EncoderBackend.Software, params PlaybackDecision[] decisions)
    {
        var plan = new PlaybackPlan(PlaybackMethod.Transcode, decisions, [], backend, [], DecodeAccelerated: false);
        var session = PlaybackSession.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "/lib/film.mkv", "matroska",
            plan, audioStreamIndex: 1, subtitleStreamIndex: null, resumePositionTicks: 0, playSessionId: null, Attached);
        session.Begin(Attached);
        var job = TranscodeJob.Prepare(session.Id, "h264", "aac", hls: true, Attached);
        job.MarkRunning("/data/transcodes/" + session.Id);
        session.AddTranscodeJob(job);
        return session;
    }
}
