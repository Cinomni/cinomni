using Cinomni.Kernel.Identifiers;
using Cinomni.Subtitles.Contracts;

namespace Cinomni.Subtitles.Persistence;

/// <summary>
/// One synchronization decision about an obtained subtitle, kept for good: what the analysis found
/// (the offset, the speed ratio, how well it matched and how clearly), what it did about it and why.
/// Append-only. A re-sync or a revert adds a row rather than editing the last one, so the history of
/// why a subtitle plays the way it does is never rewritten. The latest row is the current answer.
/// </summary>
public sealed class SubtitleSyncDecision
{
    public const int ReasonMaxLength = 500;

    public Guid Id { get; init; }

    public Guid SubtitleAssetId { get; init; }

    public SubtitleSyncOutcome Outcome { get; init; }

    /// <summary>The best offset found, in milliseconds, whether or not it was applied.</summary>
    public int OffsetMs { get; init; }

    /// <summary>The speed ratio of the best match: 1, or a 25 ↔ 23.976 fps conversion.</summary>
    public double Ratio { get; init; }

    /// <summary>How well the subtitle's cues line up with speech at the best match (phi, −1…1).</summary>
    public double Score { get; init; }

    /// <summary>How far the best match stands above the best one more than two seconds away.</summary>
    public double Margin { get; init; }

    public required string Reason { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public static SubtitleSyncDecision Create(
        Guid subtitleAssetId,
        SubtitleSyncOutcome outcome,
        int offsetMs,
        double ratio,
        double score,
        double margin,
        string reason,
        DateTimeOffset now) => new()
    {
        Id = Uuid7.New(),
        SubtitleAssetId = subtitleAssetId,
        Outcome = outcome,
        OffsetMs = offsetMs,
        Ratio = ratio,
        Score = Finite(score),
        Margin = Finite(margin),
        Reason = Text.Truncate(reason, ReasonMaxLength)!,
        CreatedAt = now,
    };

    /// <summary>A decision that measured nothing: an unsupported subtitle, a failure, a revert.</summary>
    public static SubtitleSyncDecision Unmeasured(
        Guid subtitleAssetId, SubtitleSyncOutcome outcome, string reason, DateTimeOffset now) =>
        Create(subtitleAssetId, outcome, offsetMs: 0, ratio: 1, score: 0, margin: 0, reason, now);

    // A NaN or an infinity has no meaning to a reader and does not survive every JSON writer.
    private static double Finite(double value) => double.IsFinite(value) ? value : 0;
}
