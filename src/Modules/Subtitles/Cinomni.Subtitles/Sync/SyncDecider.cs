using System.Globalization;
using Cinomni.Subtitles.Contracts;

namespace Cinomni.Subtitles.Sync;

/// <summary>What a pass concluded, with the numbers behind it and a sentence a person can read.</summary>
public sealed record SyncVerdict(
    SubtitleSyncOutcome Outcome,
    long OffsetMs,
    double Ratio,
    double Score,
    double Margin,
    string Reason)
{
    /// <summary>A verdict reached without measuring anything (unsupported input, a failure).</summary>
    public static SyncVerdict Unmeasured(SubtitleSyncOutcome outcome, string reason) =>
        new(outcome, 0, 1, 0, 0, reason);
}

/// <summary>
/// Turns the best placement the search found into a decision. A shift is applied only when all three
/// checks agree: the match is strong (<see cref="SubtitleSyncOptions.MinScore"/>), it clearly beats every
/// other placement more than two seconds away (<see cref="SubtitleSyncOptions.MinMargin"/>), and the
/// start, middle and end of the video each point at the same offset on their own
/// (<see cref="SubtitleSyncOptions.MaxWindowDisagreement"/>). Any one failing keeps the original and
/// says which, because "low confidence" alone does not tell an administrator what to look at.
/// </summary>
public static class SyncDecider
{
    /// <summary>Closer than this at the same speed is in sync already: nobody sees a tenth of a second.</summary>
    public const long InSyncToleranceMs = 100;

    /// <summary>
    /// How much clearer a match must be when the video was sampled only once. With one stretch there is
    /// no second part of the video to confirm the offset, so score and margin are all that stand between
    /// a cold open that happens to line up and a shift applied to the whole film.
    /// </summary>
    public const double SingleWindowStrictness = 1.4;

    public static SyncVerdict Decide(SyncEstimate? estimate, int windowCount, SubtitleSyncOptions options)
    {
        if (estimate is null || estimate.WindowOffsetsMs.Count == 0)
        {
            return SyncVerdict.Unmeasured(
                SubtitleSyncOutcome.LowConfidence,
                "Low confidence: the sampled parts of the video have too little speech or dialogue to compare.");
        }

        var strictness = windowCount == 1 ? SingleWindowStrictness : 1.0;
        var minScore = Math.Min(1, options.MinScore * strictness);
        var minMargin = options.MinMargin * strictness;
        var sampledOnce = windowCount == 1 ? " (a stricter bar: the video was too short to sample more than once)" : string.Empty;

        if (estimate.Score < minScore)
        {
            return LowConfidence(
                estimate,
                Invariant($"the best match scores {estimate.Score:0.00}, below the {minScore:0.00} required{sampledOnce}."));
        }

        if (estimate.Margin < minMargin)
        {
            return LowConfidence(
                estimate,
                Invariant($"the best match ({Seconds(estimate.OffsetMs)}) is not clearly better than others more than 2 s away ") +
                Invariant($"(margin {estimate.Margin:0.00}, {minMargin:0.00} required{sampledOnce})."));
        }

        var required = Math.Min(2, windowCount);
        if (estimate.WindowOffsetsMs.Count < required)
        {
            return LowConfidence(
                estimate,
                "only one sampled part of the video had enough speech to compare, so the match could not be cross-checked.");
        }

        var spread = estimate.WindowOffsetsMs.Max() - estimate.WindowOffsetsMs.Min();
        if (spread > options.MaxWindowDisagreement.TotalMilliseconds)
        {
            return LowConfidence(
                estimate,
                Invariant($"the start, middle and end of the video disagree on the offset by {spread / 1000.0:0.00} s."));
        }

        if (Math.Abs(estimate.OffsetMs) < InSyncToleranceMs && estimate.Ratio == 1.0)
        {
            return Verdict(SubtitleSyncOutcome.AlreadyInSync, estimate, $"Already in sync (best match {Seconds(estimate.OffsetMs)}).");
        }

        var retimed = estimate.Ratio == 1.0 ? string.Empty : $" and retimed from {RatioLabel(estimate.Ratio)}";
        return Verdict(SubtitleSyncOutcome.Applied, estimate, $"Shifted by {Seconds(estimate.OffsetMs)}{retimed}.");
    }

    /// <summary>"25 → 23.976 fps" for a subtitle timed for 25 fps on a 23.976 fps video, and the reverse.</summary>
    public static string RatioLabel(double ratio) => ratio > 1 ? "25 to 23.976 fps" : "23.976 to 25 fps";

    private static string Seconds(long ms) =>
        string.Create(CultureInfo.InvariantCulture, $"{ms / 1000.0:+0.00;-0.00;0.00} s");

    private static SyncVerdict LowConfidence(SyncEstimate estimate, string why) =>
        Verdict(SubtitleSyncOutcome.LowConfidence, estimate, "Low confidence: " + why);

    private static SyncVerdict Verdict(SubtitleSyncOutcome outcome, SyncEstimate estimate, string reason) =>
        new(outcome, estimate.OffsetMs, estimate.Ratio, estimate.Score, estimate.Margin, reason);

    // The numbers in a reason are formatted under the invariant culture whatever the host's is: it is
    // stored, and "0,25" would read as two numbers.
    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
