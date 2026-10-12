using Cinomni.Subtitles.Providers;

namespace Cinomni.Subtitles.Tests;

/// <summary>
/// How a subtitle candidate is judged against the title it is wanted for. The cases are the ones a real
/// library produced: a film whose release name shares a group tag with another film, an episode search
/// that also returned other shows' seventeenth episodes, and a provider rating that was 0 for nearly
/// every file and so decided nothing.
/// </summary>
public sealed class SubtitleCandidateScorerTests
{
    private const int Threshold = 40;

    private static readonly SubtitleProviderQuery Sintel = new(
        "classicalhd-sintel.2010.en-1080p", "en", HearingImpaired: false,
        Title: "Sintel", Year: 2010, ImdbId: "tt1727587");

    private static readonly SubtitleProviderQuery WorldWarEpisode = new(
        "World War II With Tom Hanks S01E17 No Surrender 1080p AMZN WEB-DL DDP2 0 H 264-RAWR", "en", HearingImpaired: false,
        SeriesTitle: "World War II with Tom Hanks", SeasonNumber: 1, EpisodeNumber: 17, ParentImdbId: "tt40385200");

    [Fact]
    public void Another_film_is_worth_nothing_however_its_name_resembles_the_video()
    {
        var themba = new CandidateEvidence(
            "themba.das.spiel.seines.lebens.2010-720p-classicalhd", Title: "Themba", Year: 2010, ImdbId: "1599369", DownloadCount: 56);

        var score = SubtitleCandidateScorer.Score(Sintel, themba);

        Assert.Equal(0, score.Score);
        Assert.Contains("Another film: Themba (2010)", score.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void The_same_film_by_imdb_id_clears_the_threshold_and_the_more_trusted_copy_ranks_first()
    {
        var plain = SubtitleCandidateScorer.Score(Sintel, new CandidateEvidence("Sintel (2010)", Title: "Sintel", Year: 2010, ImdbId: "1727587", DownloadCount: 1_496));
        var trusted = SubtitleCandidateScorer.Score(Sintel, new CandidateEvidence("sintel_en", Title: "Sintel", Year: 2010, ImdbId: "1727587", DownloadCount: 8_223, Trusted: true));

        Assert.InRange(plain.Score, SubtitleCandidateScorer.ConfirmedScore, 100);
        Assert.True(trusted.Score > plain.Score);
        Assert.StartsWith("Same film by IMDb id (tt1727587)", trusted.Reason, StringComparison.Ordinal);
        Assert.Contains("trusted uploader", trusted.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_ids_the_title_and_year_decide()
    {
        var query = Sintel with { ImdbId = null };

        Assert.InRange(SubtitleCandidateScorer.Score(query, new CandidateEvidence("Sintel.2010.Xvid-VODO", Title: "Sintel", Year: 2010)).Score, Threshold, 100);
        Assert.InRange(SubtitleCandidateScorer.Score(query, new CandidateEvidence("Sintel.2010.Xvid-VODO")).Score, Threshold, 100);
        Assert.Equal(0, SubtitleCandidateScorer.Score(query, new CandidateEvidence("Sintel", Title: "Sintel", Year: 1987)).Score);
        Assert.Equal(0, SubtitleCandidateScorer.Score(query, new CandidateEvidence("themba.2010-classicalhd")).Score);
    }

    [Fact]
    public void A_film_whose_title_is_not_known_never_clears_the_threshold_on_popularity()
    {
        var query = new SubtitleProviderQuery("Some.Movie.2024.1080p", "en", HearingImpaired: false);

        var score = SubtitleCandidateScorer.Score(query, new CandidateEvidence("Some.Movie.2024.1080p", DownloadCount: 100_000, Trusted: true));

        Assert.True(score.Score < Threshold, score.Reason);
    }

    [Fact]
    public void The_same_episode_of_the_same_series_by_imdb_id_is_confirmed()
    {
        var score = SubtitleCandidateScorer.Score(WorldWarEpisode, new CandidateEvidence(
            "World War II With Tom Hanks S01E17 No Surrender 1080p AMZN WEB-DL DDP2 0 H 264-RAWR",
            Title: "World War II with Tom Hanks", ImdbId: "43615773", ParentImdbId: "40385200", Season: 1, Episode: 17, DownloadCount: 340));

        Assert.InRange(score.Score, SubtitleCandidateScorer.ConfirmedScore + 10, 100);
        Assert.Contains("same release group RAWR", score.Reason, StringComparison.Ordinal);
        Assert.Contains("same source (WEB)", score.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Another_series_or_another_episode_is_worth_nothing()
    {
        var otherSeries = new CandidateEvidence(
            "Star.Wars.The.Clone.Wars.S01E17.720p.HDTV.x264-CTU", Title: "Star Wars: The Clone Wars", ParentImdbId: "458290", Season: 1, Episode: 17);
        var otherEpisode = new CandidateEvidence(
            "World.War.II.with.Tom.Hanks.S01E16.1080p", Title: "World War II with Tom Hanks", ParentImdbId: "40385200", Season: 1, Episode: 16);
        var unnumbered = new CandidateEvidence("World War II with Tom Hanks complete", Title: "World War II with Tom Hanks");

        Assert.Equal(0, SubtitleCandidateScorer.Score(WorldWarEpisode, otherSeries).Score);
        Assert.Contains("Another episode: S01E16, not S01E17", SubtitleCandidateScorer.Score(WorldWarEpisode, otherEpisode).Reason, StringComparison.Ordinal);
        Assert.Equal(0, SubtitleCandidateScorer.Score(WorldWarEpisode, unnumbered).Score);
    }

    [Theory]
    [InlineData("Star.Wars.The.Clone.Wars.S01E17.720p.HDTV.x264-CTU", 0)]
    [InlineData("World.Trigger.S01E17.Border.Official.Enlistment.WEBRip.NF", 0)]
    [InlineData("World.War.II.with.Tom.Hanks.S01E17.720p.HEVC.x265-MeGusta", Threshold)]
    [InlineData("World War II With Tom Hanks 1x17 No Surrender", Threshold)]
    public void Without_ids_an_episode_is_matched_on_its_numbering_and_the_series_name_in_its_release(string release, int atLeast)
    {
        var query = WorldWarEpisode with { ParentImdbId = null };

        var score = SubtitleCandidateScorer.Score(query, new CandidateEvidence(release));

        if (atLeast == 0)
        {
            Assert.Equal(0, score.Score);
        }
        else
        {
            Assert.InRange(score.Score, atLeast, 100);
        }
    }

    [Fact]
    public void Series_titles_match_across_articles_years_punctuation_and_accents()
    {
        Assert.True(SubtitleCandidateScorer.SameTitle("The Big Bang Theory", "Big Bang Theory"));
        Assert.True(SubtitleCandidateScorer.SameTitle("Doctor Who (2005)", "Doctor Who"));
        Assert.True(SubtitleCandidateScorer.SameTitle("Amélie", "Amelie"));
        Assert.True(SubtitleCandidateScorer.SameTitle("Law & Order", "Law and Order"));
        Assert.False(SubtitleCandidateScorer.SameTitle("Sintel", "Themba"));
        Assert.False(SubtitleCandidateScorer.SameTitle("Star Wars", "Star Wars: The Clone Wars"));
    }

    [Fact]
    public void A_machine_translation_sinks_below_the_threshold_even_for_the_right_film()
    {
        var score = SubtitleCandidateScorer.Score(Sintel, new CandidateEvidence("Sintel", Title: "Sintel", Year: 2010, ImdbId: "1727587", MachineTranslated: true));

        Assert.True(score.Score < Threshold, score.Reason);
        Assert.Contains("machine translated", score.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("tt0898266", "898266")]
    [InlineData("898266", "898266")]
    [InlineData("tt", null)]
    [InlineData("nm0000158", null)]
    [InlineData("tt0000000", null)]
    [InlineData("tt12&x=1", null)]
    public void Imdb_ids_are_read_as_bare_title_numbers(string id, string? number)
    {
        Assert.Equal(number, SubtitleCandidateScorer.ImdbNumber(id));
    }
}
