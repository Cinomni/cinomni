namespace Cinomni.Subtitles.Sync;

/// <summary>
/// How much a stretch of mono 8 kHz audio sounds like somebody talking, on a 10 ms grid.
/// <para>
/// Speech is told apart by its rhythm, not its loudness. A voice rises and falls with every syllable,
/// a few times a second; music, a laugh track, applause and room tone are loud too, but their level
/// moves much more slowly or much faster than that. So the measure is how strongly the level of the
/// voice band (FFmpeg's high- and low-pass filters) swings at syllable rate, 2 to 8 times a second,
/// around its own recent average.
/// </para>
/// <para>
/// The result is a continuous series, not a speech/no-speech decision: the offset search correlates it
/// with the subtitle's cues, and a threshold here would throw away exactly the contrast it needs. A
/// detector that only compared loudness with the stretch's noise floor marked two thirds of a sitcom as
/// speech, laughter and music included, and found no offset in a subtitle that was simply 2.5 s late.
/// </para>
/// </summary>
public static class SpeechDetector
{
    public const int SampleRate = 8000;

    /// <summary>Ten milliseconds: the grid every speech and cue series in the analysis shares.</summary>
    public const int FrameMs = 10;

    public const int SamplesPerFrame = SampleRate * FrameMs / 1000;

    /// <summary>The slowest and fastest level swings counted as syllables, in swings per second.</summary>
    public const double SlowestSyllableHz = 2;

    public const double FastestSyllableHz = 8;

    /// <summary>The level is measured against its own average over this many frames (one second).</summary>
    public const int BaselineFrames = 100;

    /// <summary>The strength of the swing is averaged over this many frames (400 ms, about two syllables).</summary>
    public const int StrengthFrames = 40;

    /// <summary>Half the length of the syllable-rate filter: 50 frames either side, so 2 Hz fits in it.</summary>
    private const int FilterRadius = 50;

    private const double SilenceDb = -120;

    private static readonly double[] SyllableFilter = BuildSyllableFilter();

    /// <summary>The level of each whole 10 ms frame of signed 16-bit little-endian PCM, in dBFS.</summary>
    public static double[] FrameLevels(ReadOnlySpan<byte> pcm)
    {
        var frames = pcm.Length / (SamplesPerFrame * 2);
        var levels = new double[frames];
        for (var frame = 0; frame < frames; frame++)
        {
            var sum = 0.0;
            var offset = frame * SamplesPerFrame * 2;
            for (var i = 0; i < SamplesPerFrame; i++)
            {
                double sample = (short)(pcm[offset + (i * 2)] | (pcm[offset + (i * 2) + 1] << 8));
                sum += sample * sample;
            }

            var rms = Math.Sqrt(sum / SamplesPerFrame) / 32768.0;
            levels[frame] = rms > 0 ? Math.Max(SilenceDb, 20 * Math.Log10(rms)) : SilenceDb;
        }

        return levels;
    }

    /// <summary>
    /// How strongly each frame's level swings at syllable rate: zero for silence or a steady sound,
    /// larger the more it moves like speech. Only the shape matters to the search, not the scale.
    /// </summary>
    public static double[] Activity(IReadOnlyList<double> levels)
    {
        var count = levels.Count;
        if (count == 0)
        {
            return [];
        }

        // A three-frame average first: a single 10 ms frame is too short to hold a pitch period of a
        // low voice, and its level jitters with where the period falls.
        var smoothed = CenteredMean(levels, 3);
        var baseline = CenteredMean(smoothed, BaselineFrames);
        var swing = new double[count];
        for (var i = 0; i < count; i++)
        {
            swing[i] = smoothed[i] - baseline[i];
        }

        var syllabic = new double[count];
        for (var i = 0; i < count; i++)
        {
            var sum = 0.0;
            var from = Math.Max(-FilterRadius, -i);
            var to = Math.Min(FilterRadius, count - 1 - i);
            for (var k = from; k <= to; k++)
            {
                sum += SyllableFilter[k + FilterRadius] * swing[i + k];
            }

            syllabic[i] = sum * sum;
        }

        var power = CenteredMean(syllabic, StrengthFrames);
        var activity = new double[count];
        for (var i = 0; i < count; i++)
        {
            activity[i] = Math.Sqrt(Math.Max(0, power[i]));
        }

        return activity;
    }

    /// <summary>
    /// A band-pass from <see cref="SlowestSyllableHz"/> to <see cref="FastestSyllableHz"/> over a series
    /// sampled every frame: the difference of two windowed low-passes, symmetric so it delays nothing.
    /// </summary>
    private static double[] BuildSyllableFilter()
    {
        const double framesPerSecond = 1000.0 / FrameMs;
        const double high = FastestSyllableHz / framesPerSecond;
        const double low = SlowestSyllableHz / framesPerSecond;
        var taps = new double[(2 * FilterRadius) + 1];
        for (var k = -FilterRadius; k <= FilterRadius; k++)
        {
            var window = 0.54 + (0.46 * Math.Cos(Math.PI * k / FilterRadius));
            taps[k + FilterRadius] = ((2 * high * Sinc(2 * high * k)) - (2 * low * Sinc(2 * low * k))) * window;
        }

        return taps;
    }

    private static double Sinc(double x) => x == 0 ? 1 : Math.Sin(Math.PI * x) / (Math.PI * x);

    /// <summary>The mean of each value and its neighbours, over the ones that exist near the edges.</summary>
    private static double[] CenteredMean(IReadOnlyList<double> values, int width)
    {
        var count = values.Count;
        var prefix = new double[count + 1];
        for (var i = 0; i < count; i++)
        {
            prefix[i + 1] = prefix[i] + values[i];
        }

        var before = width / 2;
        var after = width - before - 1;
        var mean = new double[count];
        for (var i = 0; i < count; i++)
        {
            var from = Math.Max(0, i - before);
            var to = Math.Min(count, i + after + 1);
            mean[i] = (prefix[to] - prefix[from]) / (to - from);
        }

        return mean;
    }
}
