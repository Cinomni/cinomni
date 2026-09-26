using Cinomni.Decision.Contracts;
using Cinomni.Decision.Evaluation;
using Cinomni.Decision.Persistence;
using Cinomni.ReleaseParsing.Contracts;
using static Cinomni.Decision.Tests.DecisionFixtures;

namespace Cinomni.Decision.Tests;

/// <summary>
/// The upgrade specification: what the evaluator does when the title is already on disk. Judging a
/// candidate then stops being "is this acceptable" and becomes "is this worth replacing what we have",
/// which is a different question with a different answer for the very same release.
/// </summary>
public sealed class UpgradeSpecificationTests
{
    private readonly ReleaseEvaluator _evaluator = new();

    private static AcquisitionProfile StandardProfile(int cutoffRank = 1000, bool upgradesAllowed = true) =>
        Profile(cutoffRank: cutoffRank, upgradesAllowed: upgradesAllowed)
            .WithQuality(QualitySource.Bluray, QualityResolution.R1080p, rank: 50)
            .WithQuality(QualitySource.WebDl, QualityResolution.R720p, rank: 25);

    [Fact]
    public void Accepts_a_release_that_beats_what_is_on_disk()
    {
        var outcome = _evaluator.Evaluate(
            Parse("The.Matrix.1999.1080p.BluRay.x264-GRP"),
            Candidate("g", "The Matrix"),
            StandardProfile(),
            current: new CurrentRelease(Rank: 25, QualityLabel: "WebDl/R720p"));

        Assert.Equal(Verdict.Accepted, outcome.Verdict);
        Assert.Contains(outcome.Reasons, r => r.Rule == "BetterThanCurrent" && r.Outcome == ReasonOutcome.Pass);
    }

    [Fact]
    public void Rejects_temporarily_a_release_that_only_matches_what_is_on_disk()
    {
        // Temporary, not permanent: the same title at a better quality may be posted tomorrow, and a
        // permanent rejection is the verdict that would stop us ever looking at this release again.
        var outcome = _evaluator.Evaluate(
            Parse("The.Matrix.1999.1080p.BluRay.x264-GRP"),
            Candidate("g", "The Matrix"),
            StandardProfile(),
            current: new CurrentRelease(Rank: 50, QualityLabel: "Bluray/R1080p"));

        Assert.Equal(Verdict.RejectedTemporary, outcome.Verdict);
        Assert.Contains(outcome.Reasons, r => r.Rule == "BetterThanCurrent" && r.Outcome == ReasonOutcome.Fail);
    }

    [Fact]
    public void Rejects_a_release_worse_than_what_is_on_disk()
    {
        var outcome = _evaluator.Evaluate(
            Parse("The.Matrix.1999.720p.WEB-DL.x264-GRP"),
            Candidate("g", "The Matrix"),
            StandardProfile(),
            current: new CurrentRelease(Rank: 50, QualityLabel: "Bluray/R1080p"));

        Assert.Equal(Verdict.RejectedTemporary, outcome.Verdict);
        Assert.Contains(outcome.Reasons, r => r.Rule == "BetterThanCurrent" && r.Outcome == ReasonOutcome.Fail);
    }

    [Fact]
    public void Accepts_nothing_once_the_cutoff_is_met()
    {
        // Even a genuinely better release: the cutoff is the household saying "good enough", and past it
        // a 2160p find is not worth a re-download.
        var outcome = _evaluator.Evaluate(
            Parse("The.Matrix.1999.1080p.BluRay.x264-GRP"),
            Candidate("g", "The Matrix"),
            StandardProfile(cutoffRank: 25),
            current: new CurrentRelease(Rank: 25, QualityLabel: "WebDl/R720p"));

        Assert.Equal(Verdict.RejectedPermanent, outcome.Verdict);
        Assert.Contains(outcome.Reasons, r => r.Rule == "CutoffNotMet" && r.Outcome == ReasonOutcome.Fail);
    }

    [Fact]
    public void Accepts_nothing_when_the_profile_forbids_upgrades()
    {
        var outcome = _evaluator.Evaluate(
            Parse("The.Matrix.1999.1080p.BluRay.x264-GRP"),
            Candidate("g", "The Matrix"),
            StandardProfile(upgradesAllowed: false),
            current: new CurrentRelease(Rank: 25, QualityLabel: "WebDl/R720p"));

        Assert.Equal(Verdict.RejectedPermanent, outcome.Verdict);
        Assert.Contains(outcome.Reasons, r => r.Rule == "UpgradesAllowedByProfile" && r.Outcome == ReasonOutcome.Fail);
    }

    [Fact]
    public void Judges_a_title_that_is_not_on_disk_exactly_as_before()
    {
        // The whole missing-title path must stay byte-for-byte what it was: no upgrade reason at all,
        // and a low quality still accepted because having the film beats not having it.
        var outcome = _evaluator.Evaluate(
            Parse("The.Matrix.1999.720p.WEB-DL.x264-GRP"),
            Candidate("g", "The Matrix"),
            StandardProfile(cutoffRank: 25),
            current: null);

        Assert.Equal(Verdict.Accepted, outcome.Verdict);
        Assert.DoesNotContain(outcome.Reasons, r =>
            r.Rule is "BetterThanCurrent" or "CutoffNotMet" or "UpgradesAllowedByProfile");
    }

    [Fact]
    public void Says_no_more_about_the_upgrade_than_the_quality_it_compared()
    {
        // The reason is what the explainability endpoint renders, so it carries both sides of the
        // comparison in the terms the user sees elsewhere.
        var outcome = _evaluator.Evaluate(
            Parse("The.Matrix.1999.1080p.BluRay.x264-GRP"),
            Candidate("g", "The Matrix"),
            StandardProfile(),
            current: new CurrentRelease(Rank: 25, QualityLabel: "WebDl/R720p"));

        var reason = Assert.Single(outcome.Reasons, r => r.Rule == "BetterThanCurrent");
        Assert.Equal("quality", reason.Property);
        Assert.Contains("WebDl/R720p", reason.ProfileValue);
        Assert.Equal("Bluray/R1080p", reason.ActualValue);
    }

    [Fact]
    public void Does_not_compare_against_the_current_release_when_the_quality_is_not_even_allowed()
    {
        // "Better than what you have" is meaningless for a quality the profile rejects outright; the
        // explanation should say the one thing that is wrong, not two.
        var outcome = _evaluator.Evaluate(
            Parse("The.Matrix.1999.HDCAM.x264-GRP"),
            Candidate("g", "The Matrix"),
            StandardProfile(),
            current: new CurrentRelease(Rank: 25, QualityLabel: "WebDl/R720p"));

        Assert.Equal(Verdict.RejectedPermanent, outcome.Verdict);
        Assert.Contains(outcome.Reasons, r => r.Rule == "QualityAllowedByProfile" && r.Outcome == ReasonOutcome.Fail);
        Assert.DoesNotContain(outcome.Reasons, r => r.Rule == "BetterThanCurrent");
    }
}
