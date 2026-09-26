using System.Globalization;
using Cinomni.Decision.Contracts;
using Cinomni.Decision.Persistence;
using Cinomni.Discovery.Contracts;
using Cinomni.ReleaseParsing.Contracts;

namespace Cinomni.Decision.Evaluation;

/// <summary>The verdict, score, ranked position and explanation of evaluating one candidate.</summary>
/// <param name="EpisodeCoverage">
/// How many of the requested catalog units this release covers. 1 for a movie or a single episode,
/// N for a season pack. Zero when nothing was requested (a manual search) or nothing matched.
/// </param>
/// <param name="CoveredUnitIds">Those units, so a selection can publish exactly what it satisfies.</param>
internal sealed record EvaluationOutcome(
    Verdict Verdict,
    int CustomFormatScore,
    IReadOnlyList<EvaluationReason> Reasons,
    int Rank,
    int EpisodeCoverage = 0,
    IReadOnlyList<Guid>? CoveredUnitIds = null)
{
    public IReadOnlyList<Guid> Covered => CoveredUnitIds ?? [];
}

/// <summary>
/// What the library already holds for the thing being searched, in the profile's own terms.
/// </summary>
/// <param name="Rank">
/// The rank of the quality on disk, on the same scale as <see cref="AllowedQuality.Rank"/>. A quality
/// the profile no longer allows resolves to <see cref="int.MinValue"/>, which makes anything allowed an
/// upgrade over it — the right answer, since the household changed its mind about that quality.
/// </param>
/// <param name="QualityLabel">How that quality reads in an explanation.</param>
public sealed record CurrentRelease(int Rank, string QualityLabel);

/// <summary>
/// The pure decision engine: scores a parsed release against a profile and produces an explainable
/// verdict (is this the thing we asked for → is it worth replacing what we already have → quality
/// allowed → size in bounds → custom-format score ≥ minimum).
/// </summary>
internal sealed class ReleaseEvaluator
{
    /// <summary>
    /// Evaluation rule-set version, persisted on every <c>release_evaluations</c> row and carried on
    /// <c>ReleaseEvaluated</c> for explainability.
    /// </summary>
    /// <remarks>
    /// 2.0.0 adds the identity checks in <see cref="EpisodeMatcher"/> — before it, no stage of the
    /// pipeline verified that a candidate was the episode that had been requested. It moves in step
    /// with <c>ReleaseParser.Version</c>: both constants are persisted per row, and letting them
    /// drift would leave the audit trail ambiguous about which change produced a given verdict.
    /// </remarks>
    public const string Version = "2.0.0";

    /// <param name="request">
    /// What the search asked for. Null (or a movie request) emits no identity reasons at all, which
    /// is what keeps a movie evaluation byte-for-byte what it always was.
    /// </param>
    /// <param name="current">
    /// What is already on disk for it, or null when nothing is. Null means the missing-title path,
    /// which is judged exactly as it always was: having the film beats not having it.
    /// </param>
    public EvaluationOutcome Evaluate(
        ParsedRelease parsed,
        ReleaseCandidate candidate,
        AcquisitionProfile profile,
        EvaluationRequest? request = null,
        CurrentRelease? current = null)
    {
        var reasons = new List<EvaluationReason>();

        // Identity first: reason order is what the explainability endpoint renders, and "this is the
        // wrong episode" is the answer a user needs before anything about quality or size.
        if (request is not null)
        {
            reasons.AddRange(EpisodeMatcher.Match(parsed, request));
        }

        var allowed = profile.AllowedQualities.FirstOrDefault(
            q => q.Source == parsed.Quality.Source && q.Resolution == parsed.Quality.Resolution);
        var qualityLabel = $"{parsed.Quality.Source}/{parsed.Quality.Resolution}";

        // Then whether we want anything at all. A user reading a rejection wants "you already have this"
        // before "and its bitrate was wrong": the second is irrelevant once the first is true.
        if (current is not null)
        {
            reasons.AddRange(UpgradeReasons(profile, current, allowed, qualityLabel));
        }

        if (allowed is null)
        {
            reasons.Add(new EvaluationReason(
                "QualityAllowedByProfile", "quality", "allowed set", qualityLabel, ReasonOutcome.Fail, RejectionKind.Permanent));
        }
        else
        {
            reasons.Add(new EvaluationReason(
                "QualityAllowedByProfile", "quality", "allowed set", qualityLabel, ReasonOutcome.Pass, null));
            reasons.Add(EvaluateSize(allowed, candidate.SizeBytes));
        }

        var score = profile.FormatRules
            .Where(rule => FormatMatcher.Matches(rule, parsed, candidate))
            .Sum(rule => rule.Score);

        var scoreValue = score.ToString(CultureInfo.InvariantCulture);
        reasons.Add(score >= profile.MinFormatScore
            ? new EvaluationReason("CustomFormatAllowedByProfile", "customFormatScore",
                $">= {profile.MinFormatScore}", scoreValue, ReasonOutcome.Pass, null)
            : new EvaluationReason("CustomFormatAllowedByProfile", "customFormatScore",
                $">= {profile.MinFormatScore}", scoreValue, ReasonOutcome.Fail, RejectionKind.Permanent));

        var covered = request?.Covered ?? [];
        return new EvaluationOutcome(
            DetermineVerdict(reasons), score, reasons, allowed?.Rank ?? int.MinValue, covered.Count, covered);
    }

    /// <summary>
    /// Why this release is, or is not, worth replacing what the library already holds. Three questions in
    /// the order they stop mattering: may we replace at all → do we still want to → is this one better.
    /// </summary>
    private static IEnumerable<EvaluationReason> UpgradeReasons(
        AcquisitionProfile profile,
        CurrentRelease current,
        AllowedQuality? allowed,
        string qualityLabel)
    {
        if (!profile.UpgradesAllowed)
        {
            // Permanent: no later candidate changes a policy, and a temporary verdict would have the
            // sweep asking again forever.
            yield return new EvaluationReason(
                "UpgradesAllowedByProfile", "upgrades", "allowed", "disabled", ReasonOutcome.Fail, RejectionKind.Permanent);
            yield break;
        }

        if (current.Rank >= profile.CutoffRank)
        {
            yield return new EvaluationReason(
                "CutoffNotMet", "quality", $"< cutoff {profile.CutoffRank}", current.Rank.ToString(CultureInfo.InvariantCulture),
                ReasonOutcome.Fail, RejectionKind.Permanent);
            yield break;
        }

        yield return new EvaluationReason(
            "CutoffNotMet", "quality", $"< cutoff {profile.CutoffRank}", current.Rank.ToString(CultureInfo.InvariantCulture),
            ReasonOutcome.Pass, null);

        // A quality the profile does not allow fails on its own account; saying it is also "not better
        // than what you have" would be two complaints about one defect.
        if (allowed is null)
        {
            yield break;
        }

        // Strictly better: an equal quality is a re-download for nothing. Temporary, because the release
        // that does beat it may simply not have been posted yet.
        yield return allowed.Rank > current.Rank
            ? new EvaluationReason(
                "BetterThanCurrent", "quality", $"> {current.QualityLabel}", qualityLabel, ReasonOutcome.Pass, null)
            : new EvaluationReason(
                "BetterThanCurrent", "quality", $"> {current.QualityLabel}", qualityLabel,
                ReasonOutcome.Fail, RejectionKind.Temporary);
    }

    private static EvaluationReason EvaluateSize(AllowedQuality allowed, long sizeBytes)
    {
        var actual = sizeBytes.ToString(CultureInfo.InvariantCulture);

        // A non-positive size means the indexer did not report one — treat it as unknown and skip
        // the bounds check rather than reject the release as "too small".
        if (sizeBytes <= 0)
        {
            return new EvaluationReason("SizeWithinLimits", "size", "unknown", actual, ReasonOutcome.Pass, null);
        }

        if (allowed.MaxSizeBytes is long max && sizeBytes > max)
        {
            return new EvaluationReason("SizeWithinLimits", "size", $"<= {max}", actual, ReasonOutcome.Fail, RejectionKind.Permanent);
        }

        if (allowed.MinSizeBytes is long min && sizeBytes < min)
        {
            return new EvaluationReason("SizeWithinLimits", "size", $">= {min}", actual, ReasonOutcome.Fail, RejectionKind.Permanent);
        }

        return new EvaluationReason("SizeWithinLimits", "size", "in range", actual, ReasonOutcome.Pass, null);
    }

    private static Verdict DetermineVerdict(IReadOnlyList<EvaluationReason> reasons)
    {
        if (reasons.All(r => r.Outcome == ReasonOutcome.Pass))
        {
            return Verdict.Accepted;
        }

        return reasons.Any(r => r.Rejection == RejectionKind.Permanent)
            ? Verdict.RejectedPermanent
            : Verdict.RejectedTemporary;
    }
}
