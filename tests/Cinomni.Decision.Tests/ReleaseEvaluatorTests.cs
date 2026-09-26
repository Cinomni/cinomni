using Cinomni.Decision.Contracts;
using Cinomni.Decision.Evaluation;
using Cinomni.ReleaseParsing.Contracts;
using static Cinomni.Decision.Tests.DecisionFixtures;

namespace Cinomni.Decision.Tests;

/// <summary>Unit tests for the pure decision engine: accept/reject/score with explainable reasons.</summary>
public sealed class ReleaseEvaluatorTests
{
    private readonly ReleaseEvaluator _evaluator = new();

    [Fact]
    public void Accepts_an_allowed_quality()
    {
        var profile = Profile().WithQuality(QualitySource.Bluray, QualityResolution.R1080p, rank: 50);

        var outcome = _evaluator.Evaluate(
            Parse("The.Matrix.1999.1080p.BluRay.x264-GRP"), Candidate("g", "The Matrix"), profile);

        Assert.Equal(Verdict.Accepted, outcome.Verdict);
        Assert.Equal(50, outcome.Rank);
        Assert.Contains(outcome.Reasons, r => r.Rule == "QualityAllowedByProfile" && r.Outcome == ReasonOutcome.Pass);
    }

    [Fact]
    public void Rejects_a_quality_absent_from_the_profile()
    {
        var profile = Profile().WithQuality(QualitySource.Bluray, QualityResolution.R1080p, rank: 50);

        var outcome = _evaluator.Evaluate(
            Parse("Movie.2020.HDCAM.x264-GRP"), Candidate("g", "Movie"), profile);

        Assert.Equal(Verdict.RejectedPermanent, outcome.Verdict);
        Assert.Contains(outcome.Reasons, r => r.Rule == "QualityAllowedByProfile" && r.Outcome == ReasonOutcome.Fail);
    }

    [Fact]
    public void Rejects_when_the_size_exceeds_the_limit()
    {
        var profile = Profile().WithQuality(QualitySource.Bluray, QualityResolution.R1080p, rank: 50, maxBytes: 1_000_000);

        var outcome = _evaluator.Evaluate(
            Parse("The.Matrix.1999.1080p.BluRay-GRP"), Candidate("g", "The Matrix", size: 2_000_000), profile);

        Assert.Equal(Verdict.RejectedPermanent, outcome.Verdict);
        Assert.Contains(outcome.Reasons, r => r.Rule == "SizeWithinLimits" && r.Outcome == ReasonOutcome.Fail);
    }

    [Fact]
    public void Sums_matching_custom_format_scores()
    {
        var profile = Profile()
            .WithQuality(QualitySource.Bluray, QualityResolution.R1080p, rank: 50)
            .WithRule("Remux", score: 50, FormatConditionType.QualityModifier, "Remux");

        var outcome = _evaluator.Evaluate(
            Parse("Movie.2020.1080p.BluRay.REMUX.AVC-GRP"), Candidate("g", "Movie"), profile);

        Assert.Equal(Verdict.Accepted, outcome.Verdict);
        Assert.Equal(50, outcome.CustomFormatScore);
    }

    [Fact]
    public void Treats_an_unknown_size_as_passing()
    {
        // The indexer reported no size (0); with a min-size bound it must not be rejected as too small.
        var profile = Profile().WithQuality(QualitySource.Bluray, QualityResolution.R1080p, rank: 50, minBytes: 1_000_000);

        var outcome = _evaluator.Evaluate(
            Parse("The.Matrix.1999.1080p.BluRay-GRP"), Candidate("g", "The Matrix", size: 0), profile);

        Assert.Equal(Verdict.Accepted, outcome.Verdict);
        Assert.Contains(outcome.Reasons, r => r.Rule == "SizeWithinLimits" && r.Outcome == ReasonOutcome.Pass);
    }

    [Fact]
    public void Rejects_below_the_minimum_format_score()
    {
        var profile = Profile(minFormatScore: 100)
            .WithQuality(QualitySource.Bluray, QualityResolution.R1080p, rank: 50);

        var outcome = _evaluator.Evaluate(
            Parse("Movie.2020.1080p.BluRay-GRP"), Candidate("g", "Movie"), profile);

        Assert.Equal(Verdict.RejectedPermanent, outcome.Verdict);
        Assert.Contains(outcome.Reasons, r => r.Rule == "CustomFormatAllowedByProfile" && r.Outcome == ReasonOutcome.Fail);
    }
}
