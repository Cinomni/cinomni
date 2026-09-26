using Cinomni.ReleaseParsing.Contracts;
using Cinomni.ReleaseParsing.Parsing;

namespace Cinomni.ReleaseParsing.Tests;

/// <summary>
/// The series-numbering battery: one table per numbering family, the canonical-key non-collapse
/// regressions (two different episodes must never share an identity) and the standalone
/// file-name surface Import consumes.
/// </summary>
public sealed class SeriesNumberingTests
{
    private readonly ReleaseParser _parser = new();
    private readonly EpisodeNumberParser _numbers = new();

    [Theory]
    [InlineData("The.Wire.S02E05.1080p.WEB-DL-GRP")]
    [InlineData("The.Wire.S02.E05.1080p.WEB-DL-GRP")]
    [InlineData("The Wire S02 E05 1080p WEB-DL-GRP")]
    [InlineData("The.Wire.s02e05.1080p.web-dl-GRP")]
    [InlineData("The_Wire_S2E5_1080p_WEB-DL-GRP")]
    [InlineData("The.Wire.S02E05v2.1080p.WEB-DL-GRP")]
    public void Parses_sxxeyy_in_every_separator_form(string title)
    {
        var parsed = _parser.Parse(title).Value;

        Assert.Equal(ReleaseType.SingleEpisode, parsed.ReleaseType);
        Assert.Equal(2, parsed.Numbering!.Season);
        Assert.Equal([5], parsed.Numbering.Episodes);
        Assert.Null(parsed.Numbering.SeasonTo);
    }

    [Theory]
    [InlineData("The.Wire.S02E05E06.1080p.WEB-DL-GRP", new[] { 5, 6 })]
    [InlineData("The.Wire.S02E05-E06.1080p.WEB-DL-GRP", new[] { 5, 6 })]
    [InlineData("The.Wire.S02E05-06.1080p.WEB-DL-GRP", new[] { 5, 6 })]
    [InlineData("The.Wire.S02E05.E06.E07.1080p.WEB-DL-GRP", new[] { 5, 6, 7 })]
    [InlineData("The.Wire.S02E05-E08.1080p.WEB-DL-GRP", new[] { 5, 6, 7, 8 })]
    public void Parses_multi_episode_lists_and_ranges(string title, int[] episodes)
    {
        var parsed = _parser.Parse(title).Value;

        Assert.Equal(ReleaseType.MultiEpisode, parsed.ReleaseType);
        Assert.Equal(2, parsed.Numbering!.Season);
        Assert.Equal(episodes, parsed.Numbering.Episodes);
    }

    [Theory]
    [InlineData("The.Wire.1x05.1080p.WEB-DL-GRP", 1, 5)]
    [InlineData("The Wire 2x10 720p HDTV-GRP", 2, 10)]
    [InlineData("The_Wire_3x07_720p_HDTV-GRP", 3, 7)]
    public void Parses_1x05_style(string title, int season, int episode)
    {
        var parsed = _parser.Parse(title).Value;

        Assert.Equal(ReleaseType.SingleEpisode, parsed.ReleaseType);
        Assert.Equal(season, parsed.Numbering!.Season);
        Assert.Equal([episode], parsed.Numbering.Episodes);
    }

    [Theory]
    [InlineData("The.Wire.S02.1080p.BluRay.x264-GRP", 2, null)]
    [InlineData("The.Wire.Season.2.1080p.BluRay.x264-GRP", 2, null)]
    [InlineData("The.Wire.Season 2.1080p.BluRay.x264-GRP", 2, null)]
    [InlineData("The.Wire.S01-S03.1080p.BluRay.x264-GRP", 1, 3)]
    [InlineData("The.Wire.S01-03.1080p.BluRay.x264-GRP", 1, 3)]
    public void Parses_season_and_multi_season_packs(string title, int season, int? seasonTo)
    {
        var parsed = _parser.Parse(title).Value;

        Assert.Equal(ReleaseType.SeasonPack, parsed.ReleaseType);
        Assert.Equal(season, parsed.Numbering!.Season);
        Assert.Equal(seasonTo, parsed.Numbering.SeasonTo);
        Assert.Empty(parsed.Numbering.Episodes);
    }

    [Fact]
    public void Parses_complete_series()
    {
        var standalone = _parser.Parse("The.Wire.Complete.Series.1080p.BluRay.x264-GRP").Value;

        Assert.Equal(ReleaseType.SeasonPack, standalone.ReleaseType);
        Assert.True(standalone.Numbering!.IsComplete);
        Assert.Equal("the-wire.complete.1080p.bluray", standalone.Identity.CanonicalKey);

        // A bare COMPLETE alongside a season range is the same marker.
        var withSeasons = _parser.Parse("The.Wire.S01-S05.COMPLETE.1080p.BluRay-GRP").Value;

        Assert.True(withSeasons.Numbering!.IsComplete);
        Assert.Equal(1, withSeasons.Numbering.Season);
        Assert.Equal(5, withSeasons.Numbering.SeasonTo);
    }

    [Fact]
    public void Parses_absolute_anime_numbering_with_v2()
    {
        var parsed = _parser.Parse("[SubsPlease] Attack on Titan - 12v2 (1080p) [A1B2C3D4].mkv").Value;

        Assert.Equal(ReleaseType.SingleEpisode, parsed.ReleaseType);
        Assert.Equal([12], parsed.Numbering!.AbsoluteEpisodes);
        Assert.Null(parsed.Numbering.Season);
        Assert.Empty(parsed.Numbering.Episodes);

        // The fansub group leads and the trailing bracket is a CRC32 checksum, never a group.
        Assert.Equal("SubsPlease", parsed.ReleaseGroup);
        Assert.Equal("attack-on-titan.e0012.1080p", parsed.Identity.CanonicalKey);
    }

    [Fact]
    public void Parses_absolute_anime_batches_within_the_range_cap()
    {
        var batch = _parser.Parse("[Erai-raws] Show Name - 01-12 [1080p][A1B2C3D4]").Value;

        Assert.Equal(ReleaseType.MultiEpisode, batch.ReleaseType);
        Assert.Equal(12, batch.Numbering!.AbsoluteEpisodes.Count);
        Assert.Equal("Erai-raws", batch.ReleaseGroup);

        // A batch wider than the cap degrades to its first number instead of expanding.
        var absurd = _parser.Parse("[Erai-raws] Show Name - 1-9999 [1080p]").Value;

        Assert.Equal([1], absurd.Numbering!.AbsoluteEpisodes);
    }

    [Theory]
    [InlineData("The.Daily.Show.2024.03.01.Guest.Name.1080p.WEB-DL-GRP", 2024, 3, 1)]
    [InlineData("Show.Name.2024-03-01.720p.HDTV-GRP", 2024, 3, 1)]
    [InlineData("Show_Name_2019_12_25_720p_HDTV-GRP", 2019, 12, 25)]
    public void Parses_date_based_episodes(string title, int year, int month, int day)
    {
        var parsed = _parser.Parse(title).Value;

        Assert.Equal(ReleaseType.SingleEpisode, parsed.ReleaseType);
        Assert.Equal(new DateOnly(year, month, day), parsed.Numbering!.AirDate);

        // The date is consumed as numbering, so it is never mistaken for the release year.
        Assert.Null(parsed.Year);
    }

    [Theory]
    [InlineData("Some.Miniseries.Part.2.1080p.BluRay.x264-GRP", 2)]
    [InlineData("Some.Miniseries.Pt.3.1080p.BluRay.x264-GRP", 3)]
    [InlineData("Some.Miniseries.Part4.1080p.BluRay.x264-GRP", 4)]
    public void Parses_part_suffix(string title, int part)
    {
        var parsed = _parser.Parse(title).Value;

        Assert.Equal(part, parsed.Numbering!.Part);
        Assert.Contains($"part{part}", parsed.Identity.CanonicalKey);
    }

    [Fact]
    public void A_series_with_a_year_keeps_both_year_and_numbering()
    {
        var parsed = _parser.Parse("The.Office.US.2005.S02E05.1080p.WEB-DL-GRP").Value;

        Assert.Equal(2005, parsed.Year);
        Assert.Equal(2, parsed.Numbering!.Season);
        Assert.Equal([5], parsed.Numbering.Episodes);
        Assert.Equal("the-office-us.2005.s02e05.1080p.webdl", parsed.Identity.CanonicalKey);
    }

    [Fact]
    public void S02E05_and_S02E06_do_not_share_a_canonical_key()
    {
        var fifth = _parser.Parse("The.Wire.S02E05.1080p.WEB-DL-GRP").Value;
        var sixth = _parser.Parse("The.Wire.S02E06.1080p.WEB-DL-GRP").Value;

        Assert.NotEqual(fifth.Identity.CanonicalKey, sixth.Identity.CanonicalKey);
    }

    [Fact]
    public void An_episode_and_its_season_pack_do_not_share_a_canonical_key()
    {
        var episode = _parser.Parse("The.Wire.S02E05.1080p.BluRay.x264-GRP").Value;
        var pack = _parser.Parse("The.Wire.S02.1080p.BluRay.x264-GRP").Value;

        Assert.NotEqual(episode.Identity.CanonicalKey, pack.Identity.CanonicalKey);
    }

    [Fact]
    public void S01E05_and_S02E05_do_not_share_a_canonical_key()
    {
        var firstSeason = _parser.Parse("The.Wire.S01E05.1080p.WEB-DL-GRP").Value;
        var secondSeason = _parser.Parse("The.Wire.S02E05.1080p.WEB-DL-GRP").Value;

        Assert.NotEqual(firstSeason.Identity.CanonicalKey, secondSeason.Identity.CanonicalKey);
    }

    [Fact]
    public void Two_dates_of_a_daily_show_do_not_share_a_canonical_key()
    {
        var first = _parser.Parse("The.Daily.Show.2024.03.01.1080p.WEB-DL-GRP").Value;
        var second = _parser.Parse("The.Daily.Show.2024.03.02.1080p.WEB-DL-GRP").Value;

        Assert.NotEqual(first.Identity.CanonicalKey, second.Identity.CanonicalKey);
    }

    [Fact]
    public void A_multi_episode_file_and_its_first_episode_do_not_share_a_canonical_key()
    {
        var single = _parser.Parse("The.Wire.S02E05.1080p.WEB-DL-GRP").Value;
        var pair = _parser.Parse("The.Wire.S02E05-E06.1080p.WEB-DL-GRP").Value;

        Assert.NotEqual(single.Identity.CanonicalKey, pair.Identity.CanonicalKey);
    }

    [Theory]
    [InlineData("Show Name - S02E05 - The Episode Title.mkv", 2, 5)]
    [InlineData("S01E02.mkv", 1, 2)]
    [InlineData("Season 03/show.name.3x11.mkv", 3, 11)]
    public void The_episode_number_parser_reads_a_bare_file_name(string fileName, int season, int episode)
    {
        // Import consumes this surface directly: no year, no scene tags, no release group.
        var numbering = _numbers.Parse(fileName);

        Assert.NotNull(numbering);
        Assert.Equal(season, numbering.Season);
        Assert.Equal([episode], numbering.Episodes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Some Movie.mkv")]
    [InlineData("sample.mkv")]
    public void The_episode_number_parser_returns_null_for_a_name_without_numbering(string fileName) =>
        Assert.Null(_numbers.Parse(fileName));

    [Fact]
    public void The_episode_number_parser_never_throws_on_hostile_input()
    {
        Assert.Null(_numbers.Parse(new string('a', 200_000)));
        Assert.Null(_numbers.Parse(new string('-', 5_000)));
    }
}
