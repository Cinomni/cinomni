namespace Cinomni.Subtitles.Sync;

/// <summary>
/// A stretch of the video's audio starting at <see cref="StartMs"/>: how speech-like each 10 ms of it is
/// (<see cref="SpeechDetector.Activity"/>).
/// </summary>
public sealed record SpeechWindow(long StartMs, double[] Speech);

/// <summary>
/// The best way the analysis found to line the subtitle up with the audio: a cue at <c>t</c> belongs at
/// <c>t × Ratio + OffsetMs</c>. <see cref="Score"/> is how well cues and speech coincide there (a
/// correlation, −1…1), <see cref="Margin"/> how far that stands above the best placement more than two
/// seconds away, and <see cref="WindowOffsetsMs"/> what each stretch of audio that matched the cues
/// clearly enough on its own preferred.
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
/// The cues become an on/off series on the same 10 ms grid as the speech activity, and each candidate
/// placement is scored by the correlation of the two: how much more speech-like the audio is while a
/// cue is showing than while none is. A coarse pass tries every 100 ms within a minute either way; a
/// fine pass tries every 10 ms around the best of those. Each frame-rate ratio is searched the same way
/// and the best overall wins.
/// </para>
/// <para>
/// A cue stays on screen after the line is spoken — a subtitler gives the reader time to finish — so
/// each one is counted only until <see cref="CueTailTrimMs"/> before it disappears. Counting the whole
/// cue pulled every estimate a few hundred milliseconds early.
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

    /// <summary>A stretch counts toward agreement only with this much cue time in it (3 s).</summary>
    public const int MinUsableFrames = 300;

    /// <summary>
    /// A stretch counts toward agreement only when its own best match reaches this correlation. A part
    /// of a film with almost no dialogue picks an offset at random, and letting it vote would refuse a
    /// subtitle the rest of the film agrees on.
    /// </summary>
    public const double MinWindowScore = 0.2;

    /// <summary>How much of the end of each cue is not counted as speech (see the class remarks).</summary>
    public const int CueTailTrimMs = 400;

    /// <summary>The least of a cue that is counted, however short it is.</summary>
    public const int MinCueMs = 200;

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
            .Select(Prepared.From)
            .OfType<Prepared>()
            .ToList();
        if (prepared.Count == 0 || cues.Count == 0)
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
            var total = default(Sums);
            for (var w = 0; w < windows.Count; w++)
            {
                var sums = series.Sum(windows[w], offset);
                perWindow[w][k] = sums.Correlation();
                total += sums;
            }

            coarse[k] = total.Correlation();
        }

        var bestCoarse = -MaxOffsetFrames + (ArgMax(coarse) * CoarseStepFrames);
        var (bestOffset, score) = Refine(bestCoarse, offset => Total(windows, series, offset).Correlation());

        // The best rival more than 2 s away, refined the way the best match was: compared at its coarse
        // 100 ms sample, a rival could read up to half a step low and make the margin look clearer than
        // it is. A refinement that drifts back inside the exclusion keeps the coarse value instead.
        var outside = double.NegativeInfinity;
        var rival = int.MinValue;
        for (var k = 0; k < coarseCount; k++)
        {
            var offset = -MaxOffsetFrames + (k * CoarseStepFrames);
            if (Math.Abs(offset - bestOffset) > MarginExclusionFrames && coarse[k] > outside)
            {
                outside = coarse[k];
                rival = offset;
            }
        }

        if (rival != int.MinValue)
        {
            var (rivalOffset, rivalScore) = Refine(rival, offset => Total(windows, series, offset).Correlation());
            if (Math.Abs(rivalOffset - bestOffset) > MarginExclusionFrames)
            {
                outside = Math.Max(outside, rivalScore);
            }
        }

        var windowOffsets = new List<long>();
        for (var w = 0; w < windows.Count; w++)
        {
            var window = windows[w];
            if (series.Sum(window, bestOffset).CueFrames < MinUsableFrames)
            {
                continue;
            }

            var own = -MaxOffsetFrames + (ArgMax(perWindow[w]) * CoarseStepFrames);
            var (ownBest, ownScore) = Refine(own, offset => series.Sum(window, offset).Correlation());
            if (ownScore >= MinWindowScore)
            {
                windowOffsets.Add((long)ownBest * Frame);
            }
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

    private static Sums Total(List<Prepared> windows, CueSeries series, int offset)
    {
        var total = default(Sums);
        foreach (var window in windows)
        {
            total += series.Sum(window, offset);
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

    /// <summary>
    /// A window on the grid, its activity standardized to mean 0 and variance 1 so that stretches of
    /// different loudness weigh the same when they are pooled. A stretch with no variation at all —
    /// digital silence, or nothing decoded — has nothing to correlate and is left out (null).
    /// </summary>
    private sealed record Prepared(int StartFrame, double[] Standardized)
    {
        public int Length => Standardized.Length;

        public static Prepared? From(SpeechWindow window)
        {
            var values = window.Speech;
            if (values.Length == 0)
            {
                return null;
            }

            var mean = values.Average();
            var variance = values.Sum(value => (value - mean) * (value - mean)) / values.Length;
            if (!(variance > 1e-12))
            {
                return null;
            }

            var deviation = Math.Sqrt(variance);
            var standardized = new double[values.Length];
            for (var i = 0; i < values.Length; i++)
            {
                standardized[i] = (values[i] - mean) / deviation;
            }

            return new Prepared((int)(window.StartMs / Frame), standardized);
        }
    }

    /// <summary>
    /// The sums behind one correlation: frames, frames with a cue showing, and the standardized activity
    /// added up over those. With the activity at mean 0 and variance 1 the correlation with an on/off
    /// series reduces to <c>Dot / √(Cue × (Frames − Cue))</c>, and pooled windows add up the same way.
    /// </summary>
    private readonly record struct Sums(long Frames, long CueFrames, double Dot)
    {
        public static Sums operator +(Sums a, Sums b) =>
            new(a.Frames + b.Frames, a.CueFrames + b.CueFrames, a.Dot + b.Dot);

        public double Correlation()
        {
            var denominator = Math.Sqrt((double)CueFrames * (Frames - CueFrames));
            return denominator <= 0 ? 0 : Dot / denominator;
        }
    }

    /// <summary>
    /// Whether a cue is showing at each frame of the stretch of timeline the search can reach, with the
    /// cues already scaled by the ratio and their tails trimmed.
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
                var spokenUntil = Math.Max(cue.StartMs + MinCueMs, cue.EndMs - CueTailTrimMs);
                var from = Math.Clamp((long)Math.Round(cue.StartMs * ratio / Frame) - origin, 0, length);
                var to = Math.Clamp((long)Math.Round(Math.Min(spokenUntil, cue.EndMs) * ratio / Frame) - origin, 0, length);
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

        /// <summary>The sums for one window with the cues moved later by <paramref name="offset"/> frames.</summary>
        public Sums Sum(Prepared window, int offset)
        {
            // Media frame m shows the cue frame m - offset.
            var first = window.StartFrame - offset - _origin;
            var dot = 0.0;
            var activity = window.Standardized;
            for (var i = 0; i < activity.Length; i++)
            {
                if (_showing[first + i] != 0)
                {
                    dot += activity[i];
                }
            }

            var cueFrames = _prefix[first + window.Length] - _prefix[first];
            return new Sums(window.Length, cueFrames, dot);
        }
    }
}
