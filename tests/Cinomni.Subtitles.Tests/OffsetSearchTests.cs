using Cinomni.Subtitles.Contracts;
using Cinomni.Subtitles.Sync;

namespace Cinomni.Subtitles.Tests;

/// <summary>
/// Pure tests of the offset search and the decision built on it, on generated dialogue. What they pin
/// is the behaviour a household depends on: a real offset is found to within two frames of the grid,
/// a PAL speed-up is recognised, and anything that merely looks like a match — random talk, a rhythm
/// that lines up every few seconds, stretches of the film that disagree — keeps the original.
/// </summary>
public sealed class OffsetSearchTests
{
    private static readonly TimeSpan Film = TimeSpan.FromHours(2);
    private static readonly SubtitleSyncOptions Options = new();

    [Theory]
    [InlineData(1_240)]
    [InlineData(-2_730)]
    [InlineData(47_350)]
    public void A_known_offset_is_recovered_within_twenty_milliseconds(long offsetMs)
    {
        // Arrange
        var dialogue = new SyntheticDialogue(seed: 7, Film);
        var windows = Windows((start, length) => dialogue.Speech(start, length, offsetMs));

        // Act
        var estimate = OffsetSearch.Estimate(windows, dialogue.Cues);
        var verdict = SyncDecider.Decide(estimate, windows.Count, Options);

        // Assert
        Assert.NotNull(estimate);
        Assert.Equal(1.0, estimate.Ratio);
        Assert.InRange(estimate.OffsetMs, offsetMs - 20, offsetMs + 20);
        Assert.Equal(SubtitleSyncOutcome.Applied, verdict.Outcome);
        Assert.StartsWith("Shifted by", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_subtitle_timed_for_25_fps_on_a_23_976_fps_video_is_found_and_retimed()
    {
        // Arrange — the lines are spoken 4.3 % later than the cues say, plus a small offset.
        var ratio = 25 * 1001 / 24000.0;
        var dialogue = new SyntheticDialogue(seed: 11, Film);
        var windows = Windows((start, length) => dialogue.Speech(start, length, 600, ratio));

        // Act
        var estimate = OffsetSearch.Estimate(windows, dialogue.Cues);
        var verdict = SyncDecider.Decide(estimate, windows.Count, Options);

        // Assert
        Assert.NotNull(estimate);
        Assert.Equal(ratio, estimate.Ratio, precision: 9);
        Assert.InRange(estimate.OffsetMs, 580, 620);
        Assert.Equal(SubtitleSyncOutcome.Applied, verdict.Outcome);
        Assert.Contains("25 to 23.976 fps", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_subtitle_already_in_sync_is_left_alone()
    {
        var dialogue = new SyntheticDialogue(seed: 3, Film);
        var windows = Windows((start, length) => dialogue.Speech(start, length, 0));

        var verdict = SyncDecider.Decide(OffsetSearch.Estimate(windows, dialogue.Cues), windows.Count, Options);

        Assert.Equal(SubtitleSyncOutcome.AlreadyInSync, verdict.Outcome);
    }

    [Fact]
    public void Speech_unrelated_to_the_cues_falls_below_the_threshold()
    {
        // Arrange — the subtitle belongs to another film.
        var dialogue = new SyntheticDialogue(seed: 19, Film);
        var windows = Windows(dialogue.RandomSpeech);

        // Act
        var estimate = OffsetSearch.Estimate(windows, dialogue.Cues);
        var verdict = SyncDecider.Decide(estimate, windows.Count, Options);

        // Assert
        Assert.NotNull(estimate);
        Assert.True(estimate.Score < Options.MinScore, $"score {estimate.Score}");
        Assert.Equal(SubtitleSyncOutcome.LowConfidence, verdict.Outcome);
    }

    [Fact]
    public void A_periodic_pattern_that_matches_every_few_seconds_has_too_little_margin()
    {
        // Arrange — one line of 1.5 s every 3 s, all film long: every 3 s offset fits equally well.
        var cues = Enumerable.Range(0, 2_000).Select(i => new SubtitleCue(i * 3_000L, (i * 3_000L) + 1_500)).ToList();
        var plan = SyncWindowPlan.For(Film.TotalSeconds, Options.WindowLength);
        var windows = plan.Select(window => Periodic(window.Start, window.Length, offsetMs: 900)).ToList();

        // Act
        var estimate = OffsetSearch.Estimate(windows, cues);
        var verdict = SyncDecider.Decide(estimate, windows.Count, Options);

        // Assert
        Assert.NotNull(estimate);
        Assert.True(estimate.Margin < Options.MinMargin, $"margin {estimate.Margin}");
        Assert.Equal(SubtitleSyncOutcome.LowConfidence, verdict.Outcome);
        Assert.Contains("not clearly better", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Stretches_of_the_film_that_disagree_keep_the_original()
    {
        // Arrange — a re-cut: the start is 2 s out, the middle 6 s, the end 11 s. No single shift and no
        // frame-rate conversion fits all three.
        var dialogue = new SyntheticDialogue(seed: 23, Film);
        var plan = SyncWindowPlan.For(Film.TotalSeconds, Options.WindowLength);
        long[] offsets = [2_000, 6_000, 11_000];
        var windows = plan.Select((window, i) => dialogue.Speech(window.Start, window.Length, offsets[i])).ToList();

        // Act
        var verdict = SyncDecider.Decide(OffsetSearch.Estimate(windows, dialogue.Cues), windows.Count, Options);

        // Assert
        Assert.Equal(SubtitleSyncOutcome.LowConfidence, verdict.Outcome);
    }

    [Fact]
    public void No_speech_or_no_cues_yields_no_estimate_and_keeps_the_original()
    {
        var dialogue = new SyntheticDialogue(seed: 5, Film);
        var silent = new SpeechWindow(0, new double[24_000]);

        Assert.Null(OffsetSearch.Estimate([], dialogue.Cues));
        Assert.Null(OffsetSearch.Estimate([silent], dialogue.Cues));
        Assert.Null(OffsetSearch.Estimate([dialogue.Speech(TimeSpan.Zero, TimeSpan.FromMinutes(4), 0)], []));
        Assert.Equal(SubtitleSyncOutcome.LowConfidence, SyncDecider.Decide(null, 3, Options).Outcome);
    }

    [Fact]
    public void A_ten_minute_video_is_sampled_twice_and_cross_checked()
    {
        // Arrange — too short for three stretches, long enough for two that do not overlap.
        var dialogue = new SyntheticDialogue(seed: 29, TimeSpan.FromMinutes(10));
        var plan = SyncWindowPlan.For(600, Options.WindowLength);
        var windows = plan.Select(window => dialogue.Speech(window.Start, window.Length, -1_500)).ToList();

        // Act
        var verdict = SyncDecider.Decide(OffsetSearch.Estimate(windows, dialogue.Cues), windows.Count, Options);

        // Assert
        Assert.Equal(2, plan.Count);
        Assert.True(plan[0].Start + plan[0].Length <= plan[1].Start);
        Assert.Equal(SubtitleSyncOutcome.Applied, verdict.Outcome);
        // Two stretches of 4 minutes hold half the dialogue three do, so the estimate is a little looser.
        Assert.InRange(verdict.OffsetMs, -1_550, -1_450);
    }

    [Fact]
    public void A_video_too_short_for_two_stretches_is_sampled_once_and_held_to_a_stricter_bar()
    {
        // Arrange — an estimate that would pass with a second stretch to confirm it.
        var plan = SyncWindowPlan.For(180, Options.WindowLength);
        var borderline = new SyncEstimate(1_000, 1.0, Options.MinScore * 1.2, Options.MinMargin * 1.2, [1_000]);

        // Act
        var once = SyncDecider.Decide(borderline, windowCount: 1, Options);
        var twice = SyncDecider.Decide(borderline with { WindowOffsetsMs = [1_000, 1_010] }, windowCount: 2, Options);

        // Assert
        Assert.Single(plan);
        Assert.Equal(SubtitleSyncOutcome.LowConfidence, once.Outcome);
        Assert.Contains("too short to sample more than once", once.Reason, StringComparison.Ordinal);
        Assert.Equal(SubtitleSyncOutcome.Applied, twice.Outcome);
    }

    [Fact]
    public void The_window_plan_samples_start_middle_and_end_clear_of_the_credits()
    {
        var plan = SyncWindowPlan.For(7_200, TimeSpan.FromMinutes(4));

        Assert.Equal(3, plan.Count);
        Assert.Equal(TimeSpan.FromMinutes(12), plan[0].Start);
        Assert.All(plan, window => Assert.Equal(TimeSpan.FromMinutes(4), window.Length));
        Assert.True(plan[2].Start + plan[2].Length <= TimeSpan.FromSeconds(7_200 * 0.9));
        Assert.Single(SyncWindowPlan.For(null, TimeSpan.FromMinutes(4)));
    }

    private static List<SpeechWindow> Windows(Func<TimeSpan, TimeSpan, SpeechWindow> speech) =>
        SyncWindowPlan.For(Film.TotalSeconds, Options.WindowLength)
            .Select(window => speech(window.Start, window.Length))
            .ToList();

    private static SpeechWindow Periodic(TimeSpan start, TimeSpan length, long offsetMs)
    {
        var frames = (int)(length.TotalMilliseconds / SpeechDetector.FrameMs);
        var speech = new bool[frames];
        var startMs = (long)start.TotalMilliseconds;
        for (var i = 0; i < frames; i++)
        {
            var media = startMs + (i * SpeechDetector.FrameMs) - offsetMs;
            speech[i] = ((media % 3_000) + 3_000) % 3_000 < 1_500;
        }

        return new SpeechWindow(startMs, speech.Select(on => on ? 1.0 : 0.0).ToArray());
    }
}
