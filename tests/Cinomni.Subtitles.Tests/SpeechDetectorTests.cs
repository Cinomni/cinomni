using Cinomni.Subtitles.Sync;

namespace Cinomni.Subtitles.Tests;

/// <summary>
/// Pure tests of speech detection on generated audio: signed 16-bit PCM at 8 kHz, the format FFmpeg is
/// asked for. A tone stands in for a voice — the detector judges loudness against the stretch's own
/// floor, not timbre.
/// </summary>
public sealed class SpeechDetectorTests
{
    private const int Rate = SpeechDetector.SampleRate;

    [Fact]
    public void Tone_bursts_over_a_noise_floor_are_speech_and_the_quiet_between_them_is_not()
    {
        // Arrange — five rounds of one second of hiss, then one second of a loud tone.
        var pcm = new List<short>();
        var random = new Random(1);
        for (var round = 0; round < 5; round++)
        {
            pcm.AddRange(Noise(random, Rate, amplitude: 40));
            pcm.AddRange(Tone(Rate, amplitude: 8_000));
        }

        // Act
        var speech = SpeechDetector.Detect(SpeechDetector.FrameLevels(Bytes(pcm)));

        // Assert — 100 frames per second: each tone second is speech; each hiss second is not, past the
        // 200 ms the hangover carries over into it.
        Assert.Equal(1_000, speech.Length);
        for (var round = 0; round < 5; round++)
        {
            var hiss = round * 200;
            var tone = hiss + 100;
            Assert.All(speech[tone..(tone + 100)], Assert.True);
            var quietFrom = round == 0 ? hiss : hiss + SpeechDetector.HangoverFrames;
            Assert.All(speech[quietFrom..tone], Assert.False);
        }
    }

    [Fact]
    public void The_hangover_carries_speech_across_the_gap_between_words()
    {
        // Arrange — two words with 150 ms between them.
        var random = new Random(2);
        var pcm = new List<short>();
        pcm.AddRange(Noise(random, Rate, 40));
        pcm.AddRange(Tone(Rate / 2, 8_000));
        pcm.AddRange(Noise(random, Rate * 15 / 100, 40));
        pcm.AddRange(Tone(Rate / 2, 8_000));
        pcm.AddRange(Noise(random, Rate, 40));

        // Act
        var speech = SpeechDetector.Detect(SpeechDetector.FrameLevels(Bytes(pcm)));

        // Assert
        Assert.All(speech[100..215], Assert.True);
    }

    [Fact]
    public void A_click_shorter_than_the_minimum_segment_is_not_speech()
    {
        var random = new Random(3);
        var pcm = new List<short>();
        pcm.AddRange(Noise(random, Rate, 40));
        pcm.AddRange(Tone(Rate * 5 / 100, 8_000)); // 50 ms
        pcm.AddRange(Noise(random, Rate, 40));

        var speech = SpeechDetector.Detect(SpeechDetector.FrameLevels(Bytes(pcm)));

        Assert.DoesNotContain(true, speech);
    }

    [Fact]
    public void Digital_silence_and_empty_input_have_no_speech()
    {
        Assert.DoesNotContain(true, SpeechDetector.Detect(SpeechDetector.FrameLevels(new byte[Rate * 4])));
        Assert.Empty(SpeechDetector.Detect(SpeechDetector.FrameLevels([])));
    }

    [Fact]
    public void A_trailing_partial_frame_is_ignored()
    {
        var levels = SpeechDetector.FrameLevels(new byte[(SpeechDetector.SamplesPerFrame * 2 * 3) + 7]);

        Assert.Equal(3, levels.Length);
    }

    private static IEnumerable<short> Tone(int samples, int amplitude) =>
        Enumerable.Range(0, samples).Select(i => (short)(amplitude * Math.Sin(2 * Math.PI * 440 * i / Rate)));

    private static IEnumerable<short> Noise(Random random, int samples, int amplitude) =>
        Enumerable.Range(0, samples).Select(_ => (short)random.Next(-amplitude, amplitude + 1));

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
