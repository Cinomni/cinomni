using Cinomni.Subtitles.Contracts;
using Cinomni.Subtitles.Sync;

namespace Cinomni.Subtitles.Tests;

/// <summary>
/// The whole analysis, PCM to verdict, on generated audio where the dialogue is never the loudest
/// thing: music plays under every line and bursts of laughter land at random. A detector that judged
/// loudness took most of a sitcom for speech and found no offset in a subtitle that was simply 2.5 s
/// late; this pins that the rhythm of syllables is still found under all of that.
/// </summary>
public sealed class SpeechUnderBackgroundTests
{
    private const int Rate = SpeechDetector.SampleRate;
    private static readonly TimeSpan Episode = TimeSpan.FromMinutes(21);

    [Theory]
    [InlineData(2_500)]
    [InlineData(-1_200)]
    public void A_late_or_early_subtitle_is_found_under_music_and_laughter(long spokenLaterMs)
    {
        // Arrange — the lines are spoken spokenLaterMs after the cues say.
        var dialogue = new SyntheticDialogue(seed: 41, Episode);
        var random = new Random(42);
        var windows = SyncWindowPlan.For(Episode.TotalSeconds, TimeSpan.FromMinutes(4))
            .Select(window => Analyze(window.Start, Audio(dialogue.Cues, window.Start, window.Length, spokenLaterMs, random)))
            .ToList();

        // Act
        var estimate = OffsetSearch.Estimate(windows, dialogue.Cues);
        var verdict = SyncDecider.Decide(estimate, windows.Count, new SubtitleSyncOptions());

        // Assert
        Assert.Equal(SubtitleSyncOutcome.Applied, verdict.Outcome);
        Assert.Equal(1.0, verdict.Ratio);
        Assert.InRange(verdict.OffsetMs, spokenLaterMs - 150, spokenLaterMs + 150);
    }

    [Fact]
    public void Cues_from_another_film_over_the_same_soundtrack_keep_the_original()
    {
        var spoken = new SyntheticDialogue(seed: 43, Episode);
        var other = new SyntheticDialogue(seed: 44, Episode);
        var random = new Random(45);
        var windows = SyncWindowPlan.For(Episode.TotalSeconds, TimeSpan.FromMinutes(4))
            .Select(window => Analyze(window.Start, Audio(spoken.Cues, window.Start, window.Length, 0, random)))
            .ToList();

        var verdict = SyncDecider.Decide(OffsetSearch.Estimate(windows, other.Cues), windows.Count, new SubtitleSyncOptions());

        Assert.Equal(SubtitleSyncOutcome.LowConfidence, verdict.Outcome);
    }

    private static SpeechWindow Analyze(TimeSpan start, short[] pcm)
    {
        var bytes = new byte[pcm.Length * 2];
        for (var i = 0; i < pcm.Length; i++)
        {
            bytes[i * 2] = (byte)(pcm[i] & 0xFF);
            bytes[(i * 2) + 1] = (byte)((pcm[i] >> 8) & 0xFF);
        }

        return new SpeechWindow((long)start.TotalMilliseconds, SpeechDetector.Activity(SpeechDetector.FrameLevels(bytes)));
    }

    /// <summary>
    /// One stretch of soundtrack: syllables wherever a line is being spoken (up to 400 ms before its
    /// cue ends), a chord held under everything, and two-second bursts of noise at random about every
    /// eight seconds — as loud as the voice, and unrelated to the cues.
    /// </summary>
    private static short[] Audio(IReadOnlyList<SubtitleCue> cues, TimeSpan start, TimeSpan length, long spokenLaterMs, Random random)
    {
        var count = (int)(length.TotalSeconds * Rate);
        var startMs = (long)start.TotalMilliseconds;
        var signal = new double[count];

        for (var i = 0; i < count; i++)
        {
            var t = (double)i / Rate;
            var tremolo = 1 + (0.2 * Math.Sin(2 * Math.PI * 0.3 * t));
            signal[i] = 2_500 * tremolo * (Math.Sin(2 * Math.PI * 330 * t) + Math.Sin(2 * Math.PI * 415 * t));
        }

        foreach (var cue in cues)
        {
            var spokenUntil = Math.Max(cue.StartMs + 200, cue.EndMs - 400);
            var from = (int)((cue.StartMs + spokenLaterMs - startMs) * Rate / 1000);
            var to = (int)((spokenUntil + spokenLaterMs - startMs) * Rate / 1000);
            var syllable = Rate / random.Next(4, 7);
            for (var i = Math.Max(0, from); i < Math.Min(count, to); i++)
            {
                if ((i - from) % syllable < syllable * 6 / 10)
                {
                    signal[i] += 6_000 * Math.Sin(2 * Math.PI * 200 * i / Rate);
                }
            }
        }

        for (var at = random.Next(0, 8 * Rate); at < count; at += random.Next(5 * Rate, 11 * Rate))
        {
            var burst = random.Next(Rate, 2 * Rate);
            for (var i = at; i < Math.Min(count, at + burst); i++)
            {
                signal[i] += random.Next(-6_000, 6_001);
            }
        }

        return signal.Select(value => (short)Math.Clamp(value, short.MinValue, short.MaxValue)).ToArray();
    }
}
