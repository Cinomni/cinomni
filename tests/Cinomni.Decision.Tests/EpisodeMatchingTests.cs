using Cinomni.Decision.Contracts;
using Cinomni.Decision.Evaluation;
using Cinomni.ReleaseParsing.Contracts;
using static Cinomni.Decision.Tests.DecisionFixtures;

namespace Cinomni.Decision.Tests;

/// <summary>
/// Unit tests for the identity checks — the filter the pipeline never had. Before these, the indexer
/// query string was the only thing standing between "search for S02E05" and "download S02E06", and a
/// wrong-episode result was accepted, downloaded, imported and marked available.
/// </summary>
public sealed class EpisodeMatchingTests
{
    private readonly ReleaseEvaluator _evaluator = new();

    [Fact]
    public void A_wrong_episode_is_rejected_permanently_with_a_matches_requested_episode_reason()
    {
        // Arrange — S02E05 was requested; the indexer answered with S02E06.
        var profile = Profile().WithQuality(QualitySource.WebDl, QualityResolution.R1080p, rank: 45);

        // Act
        var outcome = _evaluator.Evaluate(
            Parse("The.Wire.S02E06.1080p.WEB-DL.x264-GRP"),
            Candidate("g", "The Wire"),
            profile,
            Request("The Wire", season: 2, episode: 5));

        // Assert
        Assert.Equal(Verdict.RejectedPermanent, outcome.Verdict);
        var reason = Assert.Single(outcome.Reasons, r => r.Rule == "MatchesRequestedEpisode");
        Assert.Equal(ReasonOutcome.Fail, reason.Outcome);
        Assert.Equal(RejectionKind.Permanent, reason.Rejection);
        Assert.Equal("S02E05", reason.ProfileValue);
        Assert.Equal("S02E06", reason.ActualValue);
    }

    [Fact]
    public void A_wrong_series_title_is_rejected()
    {
        // Arrange
        var profile = Profile().WithQuality(QualitySource.WebDl, QualityResolution.R1080p, rank: 45);

        // Act
        var outcome = _evaluator.Evaluate(
            Parse("The.Crown.S02E05.1080p.WEB-DL.x264-GRP"),
            Candidate("g", "The Crown"),
            profile,
            Request("The Wire", season: 2, episode: 5));

        // Assert
        Assert.Equal(Verdict.RejectedPermanent, outcome.Verdict);
        var reason = Assert.Single(outcome.Reasons, r => r.Rule == "MatchesRequestedSeries");
        Assert.Equal(ReasonOutcome.Fail, reason.Outcome);
    }

    [Fact]
    public void A_matching_episode_is_accepted()
    {
        // Arrange
        var profile = Profile().WithQuality(QualitySource.WebDl, QualityResolution.R1080p, rank: 45);

        // Act
        var outcome = _evaluator.Evaluate(
            Parse("The.Wire.S02E05.1080p.WEB-DL.x264-GRP"),
            Candidate("g", "The Wire"),
            profile,
            Request("The Wire", season: 2, episode: 5));

        // Assert
        Assert.Equal(Verdict.Accepted, outcome.Verdict);
        Assert.All(
            outcome.Reasons.Where(r => r.Rule.StartsWith("MatchesRequested", StringComparison.Ordinal)),
            r => Assert.Equal(ReasonOutcome.Pass, r.Outcome));
    }

    [Fact]
    public void The_identity_reasons_come_before_the_quality_block()
    {
        // Reason order is what the explainability endpoint renders, and "wrong episode" is the answer
        // a user needs before anything about quality or size.
        var profile = Profile().WithQuality(QualitySource.WebDl, QualityResolution.R1080p, rank: 45);

        var outcome = _evaluator.Evaluate(
            Parse("The.Wire.S02E05.1080p.WEB-DL.x264-GRP"),
            Candidate("g", "The Wire"),
            profile,
            Request("The Wire", season: 2, episode: 5));

        var rules = outcome.Reasons.Select(r => r.Rule).ToList();
        Assert.Equal(
            ["MatchesRequestedSeries", "MatchesRequestedSeason", "MatchesRequestedEpisode"],
            rules.Take(3));
        Assert.True(rules.IndexOf("QualityAllowedByProfile") > rules.IndexOf("MatchesRequestedEpisode"));
    }

    [Fact]
    public void A_season_pack_satisfies_an_episode_request()
    {
        // A pack genuinely contains the requested episode, so rejecting it would be wrong.
        var profile = Profile().WithQuality(QualitySource.WebDl, QualityResolution.R1080p, rank: 45);

        var outcome = _evaluator.Evaluate(
            Parse("The.Wire.S02.1080p.WEB-DL.x264-GRP"),
            Candidate("g", "The Wire"),
            profile,
            Request("The Wire", season: 2, episode: 5));

        Assert.Equal(Verdict.Accepted, outcome.Verdict);
    }

    [Fact]
    public void A_pack_of_another_season_is_rejected()
    {
        var profile = Profile().WithQuality(QualitySource.WebDl, QualityResolution.R1080p, rank: 45);

        var outcome = _evaluator.Evaluate(
            Parse("The.Wire.S03.1080p.WEB-DL.x264-GRP"),
            Candidate("g", "The Wire"),
            profile,
            Request("The Wire", season: 2, episode: 5));

        Assert.Equal(Verdict.RejectedPermanent, outcome.Verdict);
        Assert.Contains(outcome.Reasons, r => r.Rule == "MatchesRequestedSeason" && r.Outcome == ReasonOutcome.Fail);
    }

    [Fact]
    public void A_release_with_no_numbering_cannot_satisfy_an_episode_request()
    {
        // The whole point: a movie-shaped name returned for an episode search is not the episode.
        var profile = Profile().WithQuality(QualitySource.Bluray, QualityResolution.R1080p, rank: 50);

        var outcome = _evaluator.Evaluate(
            Parse("The.Wire.2002.1080p.BluRay.x264-GRP"),
            Candidate("g", "The Wire"),
            profile,
            Request("The Wire", season: 2, episode: 5));

        Assert.Equal(Verdict.RejectedPermanent, outcome.Verdict);
        Assert.Contains(outcome.Reasons, r => r.Rule == "MatchesRequestedEpisode" && r.Outcome == ReasonOutcome.Fail);
    }

    [Fact]
    public void An_absolute_numbered_release_matches_an_absolute_request()
    {
        var profile = Profile().WithQuality(QualitySource.WebDl, QualityResolution.R1080p, rank: 45);

        var outcome = _evaluator.Evaluate(
            Parse("[SubsPlease] One Piece - 1071 (1080p) [WEB-DL]"),
            Candidate("g", "One Piece"),
            profile,
            Request("One Piece", absolute: 1071));

        Assert.Equal(Verdict.Accepted, outcome.Verdict);
        Assert.Contains(outcome.Reasons, r => r.Rule == "MatchesRequestedEpisode" && r.Outcome == ReasonOutcome.Pass);
    }

    [Fact]
    public void A_date_based_release_matches_only_its_own_date()
    {
        var profile = Profile().WithQuality(QualitySource.WebDl, QualityResolution.R1080p, rank: 45);
        var parsed = Parse("The.Daily.Show.2026.07.28.1080p.WEB-DL.x264-GRP");

        var right = _evaluator.Evaluate(
            parsed, Candidate("g", "x"), profile,
            Request("The Daily Show", airDate: new DateOnly(2026, 7, 28)));
        var wrong = _evaluator.Evaluate(
            parsed, Candidate("g", "x"), profile,
            Request("The Daily Show", airDate: new DateOnly(2026, 7, 29)));

        Assert.Equal(Verdict.Accepted, right.Verdict);
        Assert.Equal(Verdict.RejectedPermanent, wrong.Verdict);
    }

    [Fact]
    public void A_movie_evaluation_emits_no_identity_reasons_at_all()
    {
        // The movie regression: nothing about the shipped path may change shape.
        var profile = Profile().WithQuality(QualitySource.Bluray, QualityResolution.R1080p, rank: 50);

        var outcome = _evaluator.Evaluate(
            Parse("The.Matrix.1999.1080p.BluRay.x264-GRP"),
            Candidate("g", "The Matrix"),
            profile,
            Request("The Matrix", contentKind: "Movie"));

        Assert.Equal(Verdict.Accepted, outcome.Verdict);
        Assert.DoesNotContain(outcome.Reasons, r => r.Rule.StartsWith("MatchesRequested", StringComparison.Ordinal));
    }

    [Fact]
    public void A_release_type_format_condition_matches()
    {
        // FormatConditionType has an explicit `_ => false` default, so a new type that is not wired
        // into the switch would silently never match anything.
        var profile = Profile()
            .WithQuality(QualitySource.WebDl, QualityResolution.R1080p, rank: 45)
            .WithRule("Season Pack", score: 5, FormatConditionType.ReleaseType, nameof(ReleaseType.SeasonPack));

        var pack = _evaluator.Evaluate(
            Parse("The.Wire.S02.1080p.WEB-DL.x264-GRP"), Candidate("g", "x"), profile);
        var single = _evaluator.Evaluate(
            Parse("The.Wire.S02E05.1080p.WEB-DL.x264-GRP"), Candidate("g", "x"), profile);

        Assert.Equal(5, pack.CustomFormatScore);
        Assert.Equal(0, single.CustomFormatScore);
    }

    [Fact]
    public void A_release_carrying_a_region_qualifier_the_catalog_title_lacks_is_rejected()
    {
        // "The Office" in the catalog, "The.Office.US" on the indexer. This used to be accepted as a
        // prefix match, on the reading that a release may carry a disambiguator the catalog title does
        // not. That reading cannot be had for free: a region qualifier is exactly how the industry
        // names a remake apart from its original ("Shameless" / "Shameless US", "The Bridge",
        // "Skins", "Being Human"), so tolerating it hands every remake's episodes to the original's
        // work — silently, permanently, and with no downstream check to catch it.
        // The trade-off is deliberate: a false rejection is one persisted, human-readable reason on
        // the explainability endpoint; a false acceptance is a wrong file hardlinked into the library
        // and an episode marked available that will never be searched for again. See the followUps —
        // the real answer is an alternate-titles table on the catalog work, which does not exist yet.
        var profile = Profile().WithQuality(QualitySource.WebDl, QualityResolution.R1080p, rank: 45);

        var outcome = _evaluator.Evaluate(
            Parse("The.Office.US.S02E05.1080p.WEB-DL.x264-GRP"),
            Candidate("g", "The Office"),
            profile,
            Request("The Office", season: 2, episode: 5));

        Assert.Equal(Verdict.RejectedPermanent, outcome.Verdict);
        var reason = Assert.Single(outcome.Reasons, r => r.Rule == "MatchesRequestedSeries");
        Assert.Equal(ReasonOutcome.Fail, reason.Outcome);
        Assert.Equal(RejectionKind.Permanent, reason.Rejection);
    }

    [Fact]
    public void A_release_of_a_spin_off_whose_name_starts_with_the_series_is_rejected()
    {
        // The critical defect: "Star Trek" season 1 episode 1 was requested and the indexer answered
        // with the first episode of "Star Trek: Picard". Nothing downstream re-checks identity, so
        // this release would have been downloaded, hardlinked as the 1966 pilot and marked available.
        var profile = Profile().WithQuality(QualitySource.WebDl, QualityResolution.R1080p, rank: 45);

        var outcome = _evaluator.Evaluate(
            Parse("Star.Trek.Picard.S01E01.1080p.WEB-DL.x264-GROUP"),
            Candidate("g", "Star Trek"),
            profile,
            Request("Star Trek", season: 1, episode: 1));

        Assert.Equal(Verdict.RejectedPermanent, outcome.Verdict);
        var reason = Assert.Single(outcome.Reasons, r => r.Rule == "MatchesRequestedSeries");
        Assert.Equal(ReasonOutcome.Fail, reason.Outcome);
        Assert.Equal("star trek", reason.ProfileValue);
        Assert.Equal("star trek picard", reason.ActualValue);
    }

    [Fact]
    public void Coverage_is_reported_from_the_resolved_units()
    {
        // Coverage is what lets a season goal prefer the pack that closes the most missing episodes.
        var profile = Profile().WithQuality(QualitySource.WebDl, QualityResolution.R1080p, rank: 45);
        var units = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };

        var outcome = _evaluator.Evaluate(
            Parse("The.Wire.S02.1080p.WEB-DL.x264-GRP"),
            Candidate("g", "The Wire"),
            profile,
            Request("The Wire", contentKind: "Season", season: 2, covered: units));

        Assert.Equal(3, outcome.EpisodeCoverage);
        Assert.Equal(units, outcome.Covered);
    }
}
