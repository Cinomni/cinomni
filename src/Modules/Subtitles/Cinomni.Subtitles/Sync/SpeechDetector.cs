namespace Cinomni.Subtitles.Sync;

/// <summary>
/// Finds where somebody is probably speaking in a stretch of mono 8 kHz audio, on a 10 ms grid.
/// <para>
/// Deliberately simple: the audio arrives band-limited to the voice range (FFmpeg's high- and low-pass
/// filters), so a frame that is clearly louder than the quiet parts of the same stretch is taken as
/// speech. "Quiet" is measured, not assumed: it is the 20th percentile of the frame levels, so a
/// noisy recording and a clean one are judged against their own floor. This mistakes music and effects
/// for speech, and that is acceptable: what matters is that the pattern of loud and quiet lines up with
/// the subtitle's cues at the right offset better than at any other, and the confidence checks downstream
/// refuse to act when it does not.
/// </para>
/// </summary>
public static class SpeechDetector
{
    public const int SampleRate = 8000;

    /// <summary>Ten milliseconds: the grid every speech and cue series in the analysis shares.</summary>
    public const int FrameMs = 10;

    public const int SamplesPerFrame = SampleRate * FrameMs / 1000;

    /// <summary>How far above the stretch's noise floor a frame must be to count as speech.</summary>
    public const double ThresholdDb = 11;

    /// <summary>Below this a frame is silence however quiet the rest is (digital silence has no floor).</summary>
    public const double AbsoluteFloorDb = -55;

    /// <summary>Speech continues this long past the last loud frame: the gaps between words are speech.</summary>
    public const int HangoverFrames = 20;

    /// <summary>A loud burst shorter than this is a click or a slam, not a line of dialogue.</summary>
    public const int MinSegmentFrames = 15;

    private const double NoisePercentile = 0.2;
    private const double SilenceDb = -120;

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

    /// <summary>Which frames are speech, given each frame's level in dBFS.</summary>
    public static bool[] Detect(IReadOnlyList<double> levels)
    {
        var speech = new bool[levels.Count];
        if (levels.Count == 0)
        {
            return speech;
        }

        var threshold = Math.Max(NoiseFloor(levels) + ThresholdDb, AbsoluteFloorDb);
        for (var i = 0; i < levels.Count; i++)
        {
            speech[i] = levels[i] >= threshold;
        }

        DropShortSegments(speech);
        ExtendWithHangover(speech);
        return speech;
    }

    private static double NoiseFloor(IReadOnlyList<double> levels)
    {
        var sorted = levels.ToArray();
        Array.Sort(sorted);
        return sorted[(int)Math.Floor((sorted.Length - 1) * NoisePercentile)];
    }

    private static void DropShortSegments(bool[] speech)
    {
        var i = 0;
        while (i < speech.Length)
        {
            if (!speech[i])
            {
                i++;
                continue;
            }

            var start = i;
            while (i < speech.Length && speech[i])
            {
                i++;
            }

            if (i - start < MinSegmentFrames)
            {
                Array.Fill(speech, false, start, i - start);
            }
        }
    }

    private static void ExtendWithHangover(bool[] speech)
    {
        var remaining = 0;
        for (var i = 0; i < speech.Length; i++)
        {
            if (speech[i])
            {
                remaining = HangoverFrames;
            }
            else if (remaining > 0)
            {
                speech[i] = true;
                remaining--;
            }
        }
    }
}
