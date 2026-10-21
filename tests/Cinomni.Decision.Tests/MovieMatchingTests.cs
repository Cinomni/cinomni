using Cinomni.Decision.Contracts;
using Cinomni.Decision.Evaluation;
using Cinomni.ReleaseParsing.Contracts;
using static Cinomni.Decision.Tests.DecisionFixtures;

namespace Cinomni.Decision.Tests;

/// <summary>
/// The film identity gate. A public tracker searched for "Teddy" (2021) answers with every release that
/// carries the word, and before this check a film was judged on quality, size and format alone: a
/// Teddy Swims concert, three series episodes with "Teddy" in their titles and a documentary were all
/// accepted, and the concert, being 2160p, was downloaded and filed as the film. The rejected column
/// below is that search, verbatim.
/// </summary>
public sealed class MovieMatchingTests
{
    private readonly ReleaseEvaluator _evaluator = new();

    [Theory]
    [InlineData("Teddy", 2021, "Teddy Swims - Live at Coachella 2026 (2026) [WEB 2160p]")]
    [InlineData("Teddy", 2021, "Teddy Swims - Live at Lollapalooza Brasil 2025 [WEB 1080p]")]
    [InlineData("Teddy", 2021, "Atlanta S02E06 Teddy Perkins 1080p AMZN WEB-DL DDP5 1 H 264-NTb")]
    [InlineData("Teddy", 2021, "Man with a Plan S04E10 Full Metal Teddy 1080p SKST WEB-DL DD 5 1 H 264-play")]
    [InlineData("Teddy", 2021, "Mr Bean The Animated Series S01E02 Missing Teddy 1080p AMZN WEB-DL DDP2 0 H")]
    [InlineData("Teddy", 2021, "BBC Teddy Pendergrass: If You Don't Know Me 1080p HDTV x265-MVG")]
    [InlineData("Teddy", 2021, "Teddy.Bear.2012.DVDRip.XviD.RoSubbed-playXD")]
    // A series episode that only shares the film's title is not the film.
    [InlineData("Teddy", 2021, "Teddy.S01E02.1080p.WEB-DL.x264-GRP")]
    // The same title in another year is a remake, or another film altogether. The same search offered
    // the French "Teddy" (2020) for the Tamil "Teddy" (2021).
    [InlineData("Teddy", 2021, "Teddy.2020.720p.BluRay.x264-RUSTED")]
    [InlineData("Dune", 2021, "Dune.1984.1080p.BluRay.x264-GRP")]
    // A pack of two films is not the one asked for.
    [InlineData("28 Days Later", 2002, "28 Days Later 2002 & 28 Weeks Later 2007 BDRip 1080p HighCod")]
    [InlineData("28 Days Later", 2002, "The Osbournes 2002 S04E05 28 Days Later 1080p WEB DL AAC2 0 H 264-NTb")]
    [InlineData("World War Z", 2013, "World War II With Tom Hanks S01E17 No Surrender 1080p AMZN WEB-DL DDP2 0 H 264-RAWR")]
    public void A_release_that_is_not_the_requested_film_is_rejected(string title, int year, string release)
    {
        var outcome = Evaluate(title, year, release);

        Assert.Equal(Verdict.RejectedPermanent, outcome.Verdict);
        var reason = Assert.Single(outcome.Reasons, r => r.Rule == "MatchesRequestedMovie");
        Assert.Equal(ReasonOutcome.Fail, reason.Outcome);
        Assert.Equal(RejectionKind.Permanent, reason.Rejection);
    }

    [Theory]
    [InlineData("Teddy", 2021, "Teddy.2021.1080p.WEB-DL.x264-GRP")]
    [InlineData("The Matrix", 1999, "The.Matrix.1999.1080p.BluRay.x264-GRP")]
    // The same tolerances as a series title: a dropped leading article, punctuation and diacritics.
    [InlineData("The Matrix", 1999, "Matrix.1999.1080p.BluRay.x264-GRP")]
    [InlineData("Amélie", 2001, "Amelie.2001.1080p.BluRay.x264-GRP")]
    [InlineData("Mission: Impossible", 1996, "Mission.Impossible.1996.1080p.BluRay.x264-GRP")]
    [InlineData("28 Days Later", 2002, "28.Days.Later.2002.2160p.UHD.BluRay.REMUX.DV.P7.HDR.MULTi[Ben The Men]")]
    // A second title after a dash reads to the parser as an absolute episode number; it is still the film.
    [InlineData("28 Days Later", 2002, "28 Days Later - 28 Giorni Dopo 2002 1080p Bluray Ita Eng x265-NAHOM")]
    [InlineData("Dune: Part Two", 2024, "Dune.Part.Two.2024.2160p.WEB-DL.DDP5.1.x265-GRP")]
    // A release that states no year is judged on its title alone, as a series release is.
    [InlineData("The Matrix", 1999, "The.Matrix.1080p.BluRay.x264-GRP")]
    public void The_requested_film_is_accepted(string title, int year, string release)
    {
        var outcome = Evaluate(title, year, release);

        var reason = Assert.Single(outcome.Reasons, r => r.Rule == "MatchesRequestedMovie");
        Assert.Equal(ReasonOutcome.Pass, reason.Outcome);
        Assert.Equal(Verdict.Accepted, outcome.Verdict);
    }

    [Fact]
    public void The_reason_says_what_was_compared()
    {
        var outcome = Evaluate("Teddy", 2021, "Teddy Swims - Live at Coachella 2026 (2026) [WEB 2160p]");

        var reason = Assert.Single(outcome.Reasons, r => r.Rule == "MatchesRequestedMovie");
        Assert.Equal("movie", reason.Property);
        Assert.Equal("teddy (2021)", reason.ProfileValue);
        Assert.Equal("teddy swims live at coachella (2026)", reason.ActualValue);
    }

    [Fact]
    public void An_episode_named_like_the_film_says_it_is_an_episode()
    {
        var outcome = Evaluate("Teddy", 2021, "Teddy.S01E02.1080p.WEB-DL.x264-GRP");

        var reason = Assert.Single(outcome.Reasons, r => r.Rule == "MatchesRequestedMovie");
        Assert.Equal("teddy S01E02", reason.ActualValue);
    }

    [Fact]
    public void The_identity_reason_comes_before_the_quality_block()
    {
        var outcome = Evaluate("Teddy", 2021, "Teddy.2021.1080p.WEB-DL.x264-GRP");

        Assert.Equal("MatchesRequestedMovie", outcome.Reasons[0].Rule);
    }

    [Fact]
    public void A_search_with_no_title_to_compare_judges_no_identity()
    {
        // A search by external id alone carries no term; the release cannot be held to an empty title.
        var outcome = _evaluator.Evaluate(
            Parse("The.Matrix.1999.1080p.BluRay.x264-GRP"),
            Candidate("g", "The.Matrix.1999.1080p.BluRay.x264-GRP"),
            Profile().WithQuality(QualitySource.Bluray, QualityResolution.R1080p, rank: 50),
            Request("", contentKind: "Movie", year: 1999));

        Assert.DoesNotContain(outcome.Reasons, r => r.Rule == "MatchesRequestedMovie");
        Assert.Equal(Verdict.Accepted, outcome.Verdict);
    }

    [Fact]
    public void A_series_request_is_left_to_the_episode_checks()
    {
        var outcome = _evaluator.Evaluate(
            Parse("The.Wire.S02E05.1080p.WEB-DL.x264-GRP"),
            Candidate("g", "The.Wire.S02E05.1080p.WEB-DL.x264-GRP"),
            Profile().WithQuality(QualitySource.WebDl, QualityResolution.R1080p, rank: 45),
            Request("The Wire", season: 2, episode: 5));

        Assert.DoesNotContain(outcome.Reasons, r => r.Rule == "MatchesRequestedMovie");
    }

    /// <summary>Every quality allowed, so the verdict turns on identity alone.</summary>
    private EvaluationOutcome Evaluate(string title, int year, string release)
    {
        var profile = Profile();
        var rank = 1;
        foreach (var source in Enum.GetValues<QualitySource>())
        {
            foreach (var resolution in Enum.GetValues<QualityResolution>())
            {
                profile = profile.WithQuality(source, resolution, rank++);
            }
        }

        return _evaluator.Evaluate(Parse(release), Candidate("g", release), profile, Request(title, contentKind: "Movie", year: year));
    }
}
