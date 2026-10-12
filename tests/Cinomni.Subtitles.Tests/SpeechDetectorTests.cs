using Cinomni.Subtitles.Sync;

namespace Cinomni.Subtitles.Tests;

/// <summary>
/// Pure tests of speech activity on generated audio: signed 16-bit PCM at 8 kHz, the format FFmpeg is
/// asked for. A tone switched on and off a few times a second stands in for syllables; the same tone
/// held steady stands in for music, and a much faster flicker for a buzz. The detector judges the
/// rhythm of the level, not its timbre, so a tone is enough.
/// </summary>
public sealed class SpeechDetectorTests
{
    private const int Rate = SpeechDetector.SampleRate;
    private const int FramesPerSecond = 1000 / SpeechDetector.FrameMs;

    [Fact]
    public void Syllable_rate_bursts_score_far_above_a_steady_sound_at_the_same_level()
    {
        // Arrange — four seconds of "syllables" (4 per second), then four of a steady tone, then four more
        // of syllables: the steady stretch is as loud, and louder on average.
        var random = new Random(1);
        var pcm = new List<short>();
        pcm.AddRange(Syllables(random, seconds: 4, perSecond: 4));
        pcm.AddRange(Tone(Rate * 4, amplitude: 8_000));
        pcm.AddRange(Syllables(random, seconds: 4, perSecond: 4));

        // Act
        var activity = SpeechDetector.Activity(SpeechDetector.FrameLevels(Bytes(pcm)));

        // Assert — compared away from the joins, where the change itself is a swing.
        Assert.Equal(12 * FramesPerSecond, activity.Length);
        var speech = Mean(activity, 100, 300);
        var steady = Mean(activity, 500, 700);
        var speechAgain = Mean(activity, 900, 1_100);
        Assert.True(speech > 5 * steady, $"syllables {speech:0.00} against steady {steady:0.00}");
        Assert.True(speechAgain > 5 * steady, $"syllables {speechAgain:0.00} against steady {steady:0.00}");
    }

    [Fact]
    public void A_flicker_much_faster_than_syllables_scores_low()
    {
        var random = new Random(2);
        var pcm = new List<short>();
        pcm.AddRange(Syllables(random, seconds: 4, perSecond: 4));
        pcm.AddRange(Syllables(random, seconds: 4, perSecond: 25));

        var activity = SpeechDetector.Activity(SpeechDetector.FrameLevels(Bytes(pcm)));

        Assert.True(Mean(activity, 100, 300) > 3 * Mean(activity, 500, 700));
    }

    [Fact]
    public void Speech_in_a_quiet_room_scores_higher_than_the_room()
    {
        var random = new Random(3);
        var pcm = new List<short>();
        pcm.AddRange(Noise(random, Rate * 4, amplitude: 60));
        pcm.AddRange(Syllables(random, seconds: 4, perSecond: 5));
        pcm.AddRange(Noise(random, Rate * 4, amplitude: 60));

        var activity = SpeechDetector.Activity(SpeechDetector.FrameLevels(Bytes(pcm)));

        var room = Math.Max(Mean(activity, 50, 300), Mean(activity, 900, 1_150));
        Assert.True(Mean(activity, 500, 700) > 3 * room);
    }

    [Fact]
    public void Digital_silence_has_no_activity_and_empty_input_has_none_at_all()
    {
        Assert.All(SpeechDetector.Activity(SpeechDetector.FrameLevels(new byte[Rate * 4])), value => Assert.Equal(0, value, 9));
        Assert.Empty(SpeechDetector.Activity(SpeechDetector.FrameLevels([])));
    }

    [Fact]
    public void A_trailing_partial_frame_is_ignored()
    {
        var levels = SpeechDetector.FrameLevels(new byte[(SpeechDetector.SamplesPerFrame * 2 * 3) + 7]);

        Assert.Equal(3, levels.Length);
        Assert.Equal(3, SpeechDetector.Activity(levels).Length);
    }

    /// <summary>A tone switched on for half of each cycle, <paramref name="perSecond"/> cycles a second, over a faint hiss.</summary>
    private static IEnumerable<short> Syllables(Random random, int seconds, int perSecond)
    {
        var cycle = Rate / perSecond;
        for (var i = 0; i < Rate * seconds; i++)
        {
            var on = i % cycle < cycle / 2;
            var voice = on ? 8_000 * Math.Sin(2 * Math.PI * 220 * i / Rate) : 0;
            yield return (short)(voice + random.Next(-40, 41));
        }
    }

    private static IEnumerable<short> Tone(int samples, int amplitude) =>
        Enumerable.Range(0, samples).Select(i => (short)(amplitude * Math.Sin(2 * Math.PI * 220 * i / Rate)));

    private static IEnumerable<short> Noise(Random random, int samples, int amplitude) =>
        Enumerable.Range(0, samples).Select(_ => (short)random.Next(-amplitude, amplitude + 1));

    private static double Mean(double[] values, int from, int to) => values[from..to].Average();

    private static byte[] Bytes(List<short> samples)
    {
        var bytes = new byte[samples.Count * 2];
        for (var i = 0; i < samples.Count; i++)
        {
            bytes[i * 2] = (byte)(samples[i] & 0xFF);
            bytes[(i * 2) + 1] = (byte)((samples[i] >> 8) & 0xFF);
        }

        return bytes;
    }
}
