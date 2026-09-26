using Cinomni.Decision.Contracts;
using Cinomni.Decision.Evaluation;
using Cinomni.Decision.Persistence;
using Cinomni.ReleaseParsing.Contracts;
using static Cinomni.Decision.Tests.DecisionFixtures;

namespace Cinomni.Decision.Tests;

/// <summary>
/// What a <b>season</b> goal accepts. A season search states a season number and nothing else — no
/// episode number, no absolute number, no air date — so the episode check never runs and the season
/// check is the last thing standing between the goal and a release that carries no season at all.
/// <para>
/// Accepting one is not a cosmetic mistake: <c>EpisodeCoverageResolver</c> resolves no units for an
/// unnumbered release, so the selection announces none, Import filters every file out as "outside the
/// requested units", the job is rejected and the goal burns an attempt — five times, on a download
/// that can be hundreds of gigabytes, until the season is exhausted and never acquired.
/// </para>
/// </summary>
public sealed class SeasonGoalMatchingTests
{
    private readonly ReleaseEvaluator _evaluator = new();

    /// <summary>The shipped series profile shape: no maximum size, so only identity decides here.</summary>
    private static AcquisitionProfile SeriesProfile() =>
        Profile()
            .WithQuality(QualitySource.WebDl, QualityResolution.R1080p, rank: 45)
            .WithQuality(QualitySource.Bluray, QualityResolution.R1080p, rank: 50);

    [Theory]
    // A complete-series pack: IsComplete, but no season number anywhere in the name.
    [InlineData("The.Wire.Complete.Series.1080p.WEB-DL.x264-GRP")]
    // Anime absolute numbering.
    [InlineData("[SubsPlease] The Wire - 1071 (1080p) [WEB-DL]")]
    // A daily show's date numbering.
    [InlineData("The.Wire.2026.07.28.1080p.WEB-DL.x264-GRP")]
    public void A_release_with_no_season_numbering_cannot_satisfy_a_season_goal(string releaseTitle)
    {
        // Act — the season-2 pack search of a work whose season 2 is missing.
        var outcome = _evaluator.Evaluate(
            Parse(releaseTitle),
            Candidate("g", releaseTitle),
            SeriesProfile(),
            Request("The Wire", contentKind: "Season", season: 2));

        // Assert
        Assert.Equal(Verdict.RejectedPermanent, outcome.Verdict);
        var reason = Assert.Single(outcome.Reasons, r => r.Rule == "MatchesRequestedSeason");
        Assert.Equal(ReasonOutcome.Fail, reason.Outcome);
        Assert.Equal(RejectionKind.Permanent, reason.Rejection);
        Assert.Equal("2", reason.ProfileValue);
    }

    [Fact]
    public void An_anime_batch_that_covers_the_requested_episodes_is_still_accepted()
    {
        // The boundary of the rule above. An absolute-numbered batch states no season either, but it
        // does resolve to catalog units — here two of the season's missing episodes — so it is a
        // genuine answer to the pack search and none of the harm applies. Rejecting it would leave
        // every anime season that is backfilled as a pack unacquirable: the planner stamps the whole
        // season when it plans a pack, so the per-episode path never runs for it either.
        IReadOnlyList<Guid> covered = [Guid.NewGuid(), Guid.NewGuid()];

        var outcome = _evaluator.Evaluate(
            Parse("[SubsPlease] The Wire - 01-12 (1080p) [WEB-DL]"),
            Candidate("g", "batch"),
            SeriesProfile(),
            Request("The Wire", contentKind: "Season", season: 2, covered: covered));

        Assert.Equal(Verdict.Accepted, outcome.Verdict);
        Assert.Equal(
            ReasonOutcome.Pass,
            Assert.Single(outcome.Reasons, r => r.Rule == "MatchesRequestedSeason").Outcome);
    }

    [Fact]
    public void The_season_pack_that_was_asked_for_is_still_accepted()
    {
        var outcome = _evaluator.Evaluate(
            Parse("The.Wire.S02.1080p.WEB-DL.x264-GRP"),
            Candidate("g", "The.Wire.S02.1080p.WEB-DL.x264-GRP"),
            SeriesProfile(),
            Request("The Wire", contentKind: "Season", season: 2));

        Assert.Equal(Verdict.Accepted, outcome.Verdict);
    }

    [Fact]
    public void A_single_episode_of_the_requested_season_is_still_accepted()
    {
        // A season goal that only finds single episodes must still be able to take them.
        var outcome = _evaluator.Evaluate(
            Parse("The.Wire.S02E05.1080p.WEB-DL.x264-GRP"),
            Candidate("g", "The.Wire.S02E05.1080p.WEB-DL.x264-GRP"),
            SeriesProfile(),
            Request("The Wire", contentKind: "Season", season: 2));

        Assert.Equal(Verdict.Accepted, outcome.Verdict);
    }

    [Fact]
    public void An_episode_goal_still_lets_the_episode_check_adjudicate_an_unnumbered_family()
    {
        // The season check defers to the episode check when one follows, so a single mismatch is
        // reported once — as an episode mismatch — instead of twice.
        var outcome = _evaluator.Evaluate(
            Parse("[SubsPlease] The Wire - 1071 (1080p) [WEB-DL]"),
            Candidate("g", "absolute"),
            SeriesProfile(),
            Request("The Wire", contentKind: "Episode", season: 2, episode: 5));

        Assert.Equal(Verdict.RejectedPermanent, outcome.Verdict);
        Assert.Equal(
            ReasonOutcome.Pass,
            Assert.Single(outcome.Reasons, r => r.Rule == "MatchesRequestedSeason").Outcome);
        Assert.Equal(
            ReasonOutcome.Fail,
            Assert.Single(outcome.Reasons, r => r.Rule == "MatchesRequestedEpisode").Outcome);
    }
}
