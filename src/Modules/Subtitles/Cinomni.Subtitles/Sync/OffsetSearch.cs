namespace Cinomni.Subtitles.Sync;

/// <summary>A stretch of the video's audio, as speech or not on the 10 ms grid, starting at <see cref="StartMs"/>.</summary>
public sealed record SpeechWindow(long StartMs, bool[] Speech);

/// <summary>
/// The best way the analysis found to line the subtitle up with the audio: a cue at <c>t</c> belongs at
/// <c>t × Ratio + OffsetMs</c>. <see cref="Score"/> is how well cues and speech coincide there (phi,
/// −1…1), <see cref="Margin"/> how far that stands above the best placement more than two seconds away,
/// and <see cref="WindowOffsetsMs"/> what each stretch of audio with enough speech and dialogue in it
/// preferred on its own.
/// </summary>
public sealed record SyncEstimate(
    long OffsetMs,
    double Ratio,
    double Score,
    double Margin,
    IReadOnlyList<long> WindowOffsetsMs);

/// <summary>
/// Searches the offset (and the frame-rate conversion) that best lines a subtitle's cues up with the
/// speech found in a few stretches of the video.
/// <para>
/// Both sides are reduced to on/off series on the same 10 ms grid, and each candidate placement is
/// scored by the phi coefficient of the two series: how much more often "a cue is showing" and
/// "somebody is speaking" coincide than chance would make them, given how much of each there is. A
/// coarse pass tries every 100 ms within a minute either way; a fine pass tries every 10 ms around the
/// best of those. Each frame-rate ratio is searched the same way and the best overall wins.
/// </para>
/// <para>
/// Pure and deterministic. It decides nothing: <see cref="SyncDecider"/> applies the confidence rules.
/// </para>
/// </summary>
public static class OffsetSearch
{
    /// <summary>Same speed, and the two PAL conversions: 23.976 fps content at 25, and back.</summary>
    public static readonly IReadOnlyList<double> Ratios = [1.0, 25 * 1001 / 24000.0, 24000 / (25 * 1001.0)];

    public const int MaxOffsetMs = 60_000;
    public const int CoarseStepMs = 100;
    public const int FineRadiusMs = 150;

    /// <summary>The margin compares the best match with the best one at least this far from it.</summary>
    public const int MarginExclusionMs = 2_000;

    /// <summary>A stretch counts toward agreement only with this much speech and this much cue time (3 s).</summary>
    public const int MinUsableFrames = 300;

    /// <summary>Stands in for "nothing else came close at all" so the margin stays a finite number.</summary>
    public const double MarginCap = 100;

    private const int Frame = SpeechDetector.FrameMs;
    private const int MaxOffsetFrames = MaxOffsetMs / Frame;
    private const int CoarseStepFrames = CoarseStepMs / Frame;
    private const int FineRadiusFrames = FineRadiusMs / Frame;
    private const int MarginExclusionFrames = MarginExclusionMs / Frame;

    /// <summary>The best placement, or null when there is no speech or no cue to compare at all.</summary>
    public static SyncEstimate? Estimate(IReadOnlyList<SpeechWindow> windows, IReadOnlyList<SubtitleCue> cues)
    {
        var prepared = windows
            .Where(window => window.Speech.Length > 0)
            .Select(Prepared.From)
            .ToList();
        if (prepared.Count == 0 || cues.Count == 0 || prepared.All(window => window.SpeechFrames.Length == 0))
        {
            return null;
        }

        SyncEstimate? best = null;
        foreach (var ratio in Ratios)
        {
            var candidate = EstimateAt(prepared, cues, ratio);

            // Strictly better only: on a tie the earlier ratio, same speed first, wins.
            if (best is null || candidate.Score > best.Score)
            {
                best = candidate;
            }
        }

        return best;
    }

    private static SyncEstimate EstimateAt(List<Prepared> windows, IReadOnlyList<SubtitleCue> cues, double ratio)
    {
        var series = CueSeries.Build(windows, cues, ratio);
        var coarseCount = (2 * MaxOffsetFrames / CoarseStepFrames) + 1;

        // Coarse pass: the overall score and each window's own, at every 100 ms.
        var coarse = new double[coarseCount];
        var perWindow = new double[windows.Count][];
        for (var w = 0; w < windows.Count; w++)
        {
            perWindow[w] = new double[coarseCount];
        }

        for (var k = 0; k < coarseCount; k++)
        {
            var offset = -MaxOffsetFrames + (k * CoarseStepFrames);
            var total = default(Counts);
            for (var w = 0; w < windows.Count; w++)
            {
                var counts = series.Count(windows[w], offset);
                perWindow[w][k] = counts.Phi();
                total += counts;
            }

            coarse[k] = total.Phi();
        }

        var bestCoarse = -MaxOffsetFrames + (ArgMax(coarse) * CoarseStepFrames);
        var (bestOffset, score) = Refine(bestCoarse, offset => Sum(windows, series, offset).Phi());

        var outside = double.NegativeInfinity;
        for (var k = 0; k < coarseCount; k++)
        {
            var offset = -MaxOffsetFrames + (k * CoarseStepFrames);
            if (Math.Abs(offset - bestOffset) > MarginExclusionFrames)
            {
                outside = Math.Max(outside, coarse[k]);
            }
        }

        var windowOffsets = new List<long>();
        for (var w = 0; w < windows.Count; w++)
        {
            var window = windows[w];
            if (window.SpeechFrames.Length < MinUsableFrames
                || series.Count(window, bestOffset).CueFrames < MinUsableFrames)
            {
                continue;
            }

            var own = -MaxOffsetFrames + (ArgMax(perWindow[w]) * CoarseStepFrames);
            var (ownBest, _) = Refine(own, offset => series.Count(window, offset).Phi());
            windowOffsets.Add((long)ownBest * Frame);
        }

        return new SyncEstimate((long)bestOffset * Frame, ratio, score, Margin(score, outside), windowOffsets);
    }

    private static double Margin(double score, double outside)
    {
        if (score <= 0)
        {
            return 0;
        }

        return outside <= 1e-6 ? MarginCap : Math.Min(MarginCap, score / outside);
    }

    /// <summary>The best offset within the fine radius of <paramref name="center"/>, every 10 ms.</summary>
    private static (int Offset, double Score) Refine(int center, Func<int, double> score)
    {
        var bestOffset = center;
        var bestScore = score(center);
        for (var offset = center - FineRadiusFrames; offset <= center + FineRadiusFrames; offset++)
        {
            if (Math.Abs(offset) > MaxOffsetFrames + FineRadiusFrames || offset == center)
            {
                continue;
            }

            var value = score(offset);
            if (value > bestScore || (value == bestScore && Math.Abs(offset) < Math.Abs(bestOffset)))
            {
                bestOffset = offset;
                bestScore = value;
            }
        }

        return (bestOffset, bestScore);
    }

    private static Counts Sum(List<Prepared> windows, CueSeries series, int offset)
    {
        var total = default(Counts);
        foreach (var window in windows)
        {
            total += series.Count(window, offset);
        }

        return total;
    }

    private static int ArgMax(double[] values)
    {
        var best = 0;
        for (var i = 1; i < values.Length; i++)
        {
            if (values[i] > values[best])
            {
                best = i;
            }
        }

        return best;
    }

    /// <summary>A window on the grid: where it starts and the frames in it that are speech.</summary>
    private sealed record Prepared(int StartFrame, int Length, int[] SpeechFrames)
    {
        public static Prepared From(SpeechWindow window)
        {
            var speech = new List<int>();
            for (var i = 0; i < window.Speech.Length; i++)
            {
                if (window.Speech[i])
                {
                    speech.Add(i);
                }
            }

            return new Prepared((int)(window.StartMs / Frame), window.Speech.Length, speech.ToArray());
        }
    }

    /// <summary>The 2×2 table behind one phi: frames, speech frames, cue frames, and both at once.</summary>
    private readonly record struct Counts(long Frames, long SpeechFrames, long CueFrames, long Both)
    {
        public static Counts operator +(Counts a, Counts b) =>
            new(a.Frames + b.Frames, a.SpeechFrames + b.SpeechFrames, a.CueFrames + b.CueFrames, a.Both + b.Both);

        public double Phi()
        {
            double n = Frames;
            double speech = SpeechFrames;
            double cue = CueFrames;
            var denominator = Math.Sqrt(speech * (n - speech) * cue * (n - cue));
            return denominator <= 0 ? 0 : ((n * Both) - (speech * cue)) / denominator;
        }
    }

    /// <summary>
    /// Whether a cue is showing at each frame of the stretch of timeline the search can reach, with the
    /// cues already scaled by the ratio, plus a running count so any window's cue time is one subtraction.
    /// </summary>
    private sealed class CueSeries
    {
        private readonly int _origin;
        private readonly byte[] _showing;
        private readonly int[] _prefix;

        private CueSeries(int origin, byte[] showing)
        {
            _origin = origin;
            _showing = showing;
            _prefix = new int[showing.Length + 1];
            for (var i = 0; i < showing.Length; i++)
            {
                _prefix[i + 1] = _prefix[i] + showing[i];
            }
        }

        public static CueSeries Build(List<Prepared> windows, IReadOnlyList<SubtitleCue> cues, double ratio)
        {
            var reach = MaxOffsetFrames + FineRadiusFrames + 1;
            var origin = windows.Min(window => window.StartFrame) - reach;
            var end = windows.Max(window => window.StartFrame + window.Length) + reach;
            var length = end - origin;

            var delta = new int[length + 1];
            foreach (var cue in cues)
            {
                var from = Math.Clamp((long)Math.Round(cue.StartMs * ratio / Frame) - origin, 0, length);
                var to = Math.Clamp((long)Math.Round(cue.EndMs * ratio / Frame) - origin, 0, length);
                if (to > from)
                {
                    delta[from]++;
                    delta[to]--;
                }
            }

            var showing = new byte[length];
            var open = 0;
            for (var i = 0; i < length; i++)
            {
                open += delta[i];
                showing[i] = open > 0 ? (byte)1 : (byte)0;
            }

            return new CueSeries(origin, showing);
        }

        /// <summary>The table for one window with the cues moved later by <paramref name="offset"/> frames.</summary>
        public Counts Count(Prepared window, int offset)
        {
            // Media frame m shows the cue frame m - offset.
            var first = window.StartFrame - offset - _origin;
            var both = 0L;
            foreach (var frame in window.SpeechFrames)
            {
                both += _showing[first + frame];
            }

            var cueFrames = _prefix[first + window.Length] - _prefix[first];
            return new Counts(window.Length, window.SpeechFrames.Length, cueFrames, both);
        }
    }
}
