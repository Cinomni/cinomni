using Cinomni.Decision.Contracts;
using Cinomni.Decision.Evaluation;
using Cinomni.ReleaseParsing.Contracts;
using static Cinomni.Decision.Tests.DecisionFixtures;

namespace Cinomni.Decision.Tests;

/// <summary>
/// The series identity gate, both directions. Decision is the <em>only</em> place in the pipeline
/// that checks a release is the show that was asked for — Downloads, Import and Library all trust
/// the selection — so a title rule that accepts a spin-off, a reboot or a regional remake links a
/// stranger's episode to the requested work, marks it available and closes the goal for good.
/// <para>
/// The table below is the contract: the left column is what the catalog asked for, the right column
/// is what an indexer answered with, and every pair states whether that answer is the same show.
/// </para>
/// </summary>
public sealed class SeriesTitleIdentityTests
{
    private readonly ReleaseEvaluatorHarness _harness = new();

    [Theory]
    // The defect: a longer name that starts with the requested one is a different show.
    [InlineData("Star Trek", "Star.Trek.Picard.S01E01.1080p.WEB-DL.x264-GRP")]
    [InlineData("The Wire", "The.Wire.Tap.S01E01.1080p.WEB-DL.x264-GRP")]
    [InlineData("Doctor Who", "Doctor.Who.Confidential.S01E01.1080p.WEB-DL.x264-GRP")]
    [InlineData("The Walking Dead", "The.Walking.Dead.World.Beyond.S01E01.1080p.WEB-DL.x264-GRP")]
    // ...and so is a shorter one the requested name starts with (the rule matched either direction).
    [InlineData("Star Trek Picard", "Star.Trek.S01E01.1080p.WEB-DL.x264-GRP")]
    [InlineData("The Walking Dead World Beyond", "The.Walking.Dead.S01E01.1080p.WEB-DL.x264-GRP")]
    // A region qualifier separates a remake from its original; "Shameless" (UK) and "Shameless US"
    // are two catalogued shows, and so are "The Office" and "The Office US".
    [InlineData("Shameless", "Shameless.US.S01E01.1080p.WEB-DL.x264-GRP")]
    [InlineData("The Office", "The.Office.US.S01E01.1080p.WEB-DL.x264-GRP")]
    public void A_release_for_a_different_show_is_rejected(string requestedTitle, string releaseTitle)
    {
        var reason = _harness.SeriesReasonFor(requestedTitle, releaseTitle);

        Assert.Equal(ReasonOutcome.Fail, reason.Outcome);
        Assert.Equal(RejectionKind.Permanent, reason.Rejection);
    }

    [Theory]
    // The plain case.
    [InlineData("The Wire", "The.Wire.S01E01.1080p.WEB-DL.x264-GRP")]
    // Diacritics: the catalog title carries them, the release does not.
    [InlineData("Pokémon", "Pokemon.S01E01.1080p.WEB-DL.x264-GRP")]
    // A possessive apostrophe is punctuation on one side and a letter on the other.
    [InlineData("Bob's Burgers", "Bobs.Burgers.S01E01.1080p.WEB-DL.x264-GRP")]
    [InlineData("Marvel's Daredevil", "Marvels.Daredevil.S01E01.1080p.WEB-DL.x264-GRP")]
    // "&" is punctuation and vanishes when the title is folded; "and" is a word and survives.
    [InlineData("Law & Order", "Law.and.Order.S01E01.1080p.WEB-DL.x264-GRP")]
    [InlineData("Law and Order", "Law.&.Order.S01E01.1080p.WEB-DL.x264-GRP")]
    // Scene names drop the leading article often enough that keeping it would reject the show.
    [InlineData("The Walking Dead", "Walking.Dead.S01E01.1080p.WEB-DL.x264-GRP")]
    // The catalog disambiguates a reboot with a trailing year; the parser lifts it out of the
    // release title into ParsedRelease.Year, so the two title keys must still agree.
    [InlineData("Doctor Who (2005)", "Doctor.Who.2005.S01E01.1080p.WEB-DL.x264-GRP")]
    // ...but a title that *is* a year is a title, not a qualifier.
    [InlineData("1923", "1923.S01E01.1080p.WEB-DL.x264-GRP")]
    public void A_release_for_the_same_show_written_differently_is_accepted(string requestedTitle, string releaseTitle)
    {
        var reason = _harness.SeriesReasonFor(requestedTitle, releaseTitle);

        Assert.Equal(ReasonOutcome.Pass, reason.Outcome);
    }

    [Fact]
    public void A_reboot_with_the_same_title_is_rejected_on_its_year()
    {
        // "Doctor Who" (1963) and "Doctor Who" (2005) share a title exactly, so the title key cannot
        // separate them; the release states 2005 and the catalog work is the 1963 one.
        var reason = _harness.SeriesReasonFor(
            "Doctor Who", "Doctor.Who.2005.S01E01.1080p.WEB-DL.x264-GRP", requestedYear: 1963);

        Assert.Equal(ReasonOutcome.Fail, reason.Outcome);
        Assert.Equal(RejectionKind.Permanent, reason.Rejection);
        Assert.Equal("doctor who (1963)", reason.ProfileValue);
        Assert.Equal("doctor who (2005)", reason.ActualValue);
    }

    [Fact]
    public void The_matching_reboot_is_accepted_on_its_year()
    {
        var reason = _harness.SeriesReasonFor(
            "Doctor Who", "Doctor.Who.2005.S01E01.1080p.WEB-DL.x264-GRP", requestedYear: 2005);

        Assert.Equal(ReasonOutcome.Pass, reason.Outcome);
    }

    [Fact]
    public void A_release_that_states_no_year_is_not_rejected_for_it()
    {
        // Most series releases carry no year at all. An unknown year is not evidence of anything,
        // and treating it as a mismatch would reject the common case.
        var reason = _harness.SeriesReasonFor(
            "The Wire", "The.Wire.S01E01.1080p.WEB-DL.x264-GRP", requestedYear: 2002);

        Assert.Equal(ReasonOutcome.Pass, reason.Outcome);
        Assert.Equal("wire", reason.ProfileValue);
        Assert.Equal("wire", reason.ActualValue);
    }

    /// <summary>Evaluates one candidate and hands back the identity reason under test.</summary>
    private sealed class ReleaseEvaluatorHarness
    {
        private readonly ReleaseEvaluator _evaluator = new();

        public EvaluationReason SeriesReasonFor(string requestedTitle, string releaseTitle, int? requestedYear = null)
        {
            var profile = Profile().WithQuality(QualitySource.WebDl, QualityResolution.R1080p, rank: 45);

            var outcome = _evaluator.Evaluate(
                Parse(releaseTitle),
                Candidate("g", releaseTitle),
                profile,
                Request(requestedTitle, season: 1, episode: 1, year: requestedYear));

            return Assert.Single(outcome.Reasons, r => r.Rule == "MatchesRequestedSeries");
        }
    }
}
