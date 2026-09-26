using Cinomni.Playback.Contracts;
using Cinomni.Playback.Persistence;

namespace Cinomni.Playback.Tests;

/// <summary>Pure unit tests for the transcode job's lifecycle and fallback bookkeeping (no database).</summary>
public sealed class TranscodeJobTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_prepared_job_has_not_fallen_back()
    {
        var job = TranscodeJob.Prepare(Guid.NewGuid(), "h264_vaapi", "aac", hls: true, Now);

        Assert.False(job.FellBackToSoftware);
        Assert.Null(job.FallbackReason);
        Assert.Equal(TranscodeState.Preparing, job.State);
    }

    [Fact]
    public void Recording_a_fallback_sets_the_flag_and_the_reason_without_changing_state()
    {
        var job = TranscodeJob.Prepare(Guid.NewGuid(), "h264_vaapi", "aac", hls: true, Now);
        job.MarkRunning("/data/transcodes/session-1");

        job.RecordFallback("hardware backend 'Vaapi' failed; fell back to 'Software'");

        Assert.True(job.FellBackToSoftware);
        Assert.Equal("hardware backend 'Vaapi' failed; fell back to 'Software'", job.FallbackReason);
        // The job still ran — only the backend that produced it differs from what was planned.
        Assert.Equal(TranscodeState.Running, job.State);
    }

    [Fact]
    public void Ffmpeg_finishing_the_file_completes_a_running_job()
    {
        var job = Running();

        Assert.True(job.RecordExit(0, string.Empty));

        Assert.Equal(TranscodeState.Completed, job.State);
        Assert.Null(job.LastError);
    }

    [Fact]
    public void Ffmpeg_failing_part_way_fails_the_job_with_the_end_of_what_it_said()
    {
        var job = Running();
        // Long enough that only its end fits the column — and the end is where the reason is.
        var diagnostics = new string('x', 2_000) + "\nError while decoding stream #0:0";

        Assert.True(job.RecordExit(1, diagnostics));

        Assert.Equal(TranscodeState.Failed, job.State);
        Assert.StartsWith("FFmpeg exited with code 1. ", job.LastError, StringComparison.Ordinal);
        Assert.EndsWith("Error while decoding stream #0:0", job.LastError, StringComparison.Ordinal);
        Assert.True(job.LastError!.Length <= 500);
    }

    [Fact]
    public void An_exit_seen_after_the_output_was_reclaimed_changes_nothing()
    {
        var job = Running();
        Assert.True(job.MarkCleaned());

        Assert.False(job.RecordExit(1, "late"));

        Assert.Equal(TranscodeState.Cleaned, job.State);
        Assert.Null(job.LastError);
    }

    [Fact]
    public void Reclaiming_the_output_cleans_a_running_or_completed_job_once()
    {
        var running = Running();
        var completed = Running();
        completed.RecordExit(0, string.Empty);

        Assert.True(running.MarkCleaned());
        Assert.True(completed.MarkCleaned());
        Assert.False(running.MarkCleaned());

        Assert.Equal(TranscodeState.Cleaned, running.State);
        Assert.Equal(TranscodeState.Cleaned, completed.State);
    }

    [Fact]
    public void A_failed_job_keeps_its_state_and_reason_when_its_output_is_reclaimed()
    {
        var job = Running();
        job.RecordExit(1, "Error while decoding");

        Assert.False(job.MarkCleaned());

        Assert.Equal(TranscodeState.Failed, job.State);
        Assert.Contains("Error while decoding", job.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public void A_fallback_reason_longer_than_the_column_is_truncated_rather_than_rejected()
    {
        var job = TranscodeJob.Prepare(Guid.NewGuid(), "h264_vaapi", "aac", hls: true, Now);
        var longReason = new string('x', 1000);

        job.RecordFallback(longReason);

        Assert.True(job.FallbackReason!.Length <= 500);
    }

    private static TranscodeJob Running()
    {
        var job = TranscodeJob.Prepare(Guid.NewGuid(), "libx264", "aac", hls: true, Now);
        job.MarkRunning("/data/transcodes/session-1");
        return job;
    }
}
