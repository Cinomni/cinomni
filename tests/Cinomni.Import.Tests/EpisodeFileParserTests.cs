using Cinomni.Import.Application;
using Cinomni.ReleaseParsing.Parsing;

namespace Cinomni.Import.Tests;

/// <summary>
/// Pure unit tests for the file-name reader. It is a thin wrapper over the platform's single
/// numbering vocabulary (<c>IEpisodeNumberParser</c>, exercised here with the real implementation),
/// so what these tests actually pin down is the two things Import adds on top: the <c>Season NN/</c>
/// folder context and the sample/extras rejection.
/// </summary>
public sealed class EpisodeFileParserTests
{
    private readonly EpisodeFileParser _parser = new(new EpisodeNumberParser());

    [Theory]
    [InlineData("/staging/The.Wire.S02E05.1080p.BluRay.x264.mkv")]
    [InlineData("/staging/The Wire - s02e05 - Undertow.mkv")]
    [InlineData("/staging/The_Wire_S02.E05_1080p.mkv")]
    public void Parses_sxxeyy_from_a_file_name(string path)
    {
        var parsed = _parser.Parse(path);

        Assert.NotNull(parsed);
        Assert.Equal(2, parsed!.SeasonNumber);
        Assert.Equal([5], parsed.EpisodeNumbers);
    }

    [Theory]
    [InlineData("/staging/Show.S01E01E02.1080p.mkv")]
    [InlineData("/staging/Show.S01E01-E02.1080p.mkv")]
    public void Parses_multi_episode_files(string path)
    {
        var parsed = _parser.Parse(path);

        Assert.NotNull(parsed);
        Assert.Equal(1, parsed!.SeasonNumber);
        Assert.Equal([1, 2], parsed.EpisodeNumbers);
    }

    [Fact]
    public void Parses_1x05()
    {
        var parsed = _parser.Parse("/staging/Show.1x05.HDTV.mkv");

        Assert.NotNull(parsed);
        Assert.Equal(1, parsed!.SeasonNumber);
        Assert.Equal([5], parsed.EpisodeNumbers);
    }

    [Fact]
    public void Uses_the_season_folder_as_context()
    {
        // A bare number in the file name is absolute numbering on its own; inside "Season 02/" it is
        // that season's episode number. Only Import knows the folder, which is why it lives here.
        var parsed = _parser.Parse("/staging/The Wire/Season 02/The Wire - 05.mkv");

        Assert.NotNull(parsed);
        Assert.Equal(2, parsed!.SeasonNumber);
        Assert.Equal([5], parsed.EpisodeNumbers);
        Assert.Empty(parsed.AbsoluteNumbers);
    }

    [Fact]
    public void An_explicit_season_in_the_file_name_beats_the_folder()
    {
        var parsed = _parser.Parse("/staging/The Wire/Season 02/The.Wire.S03E07.mkv");

        Assert.Equal(3, _parser.Parse("/staging/The.Wire.S03E07.mkv")!.SeasonNumber);
        Assert.NotNull(parsed);
        Assert.Equal(3, parsed!.SeasonNumber);
        Assert.Equal([7], parsed.EpisodeNumbers);
    }

    [Fact]
    public void A_specials_folder_is_season_zero()
    {
        var parsed = _parser.Parse("/staging/The Wire/Specials/The Wire - 02.mkv");

        Assert.NotNull(parsed);
        Assert.Equal(EpisodeFileParser.SpecialsSeasonNumber, parsed!.SeasonNumber);
        Assert.Equal([2], parsed.EpisodeNumbers);
    }

    [Fact]
    public void Parses_absolute_numbering()
    {
        // No season folder → the number stays absolute, which is what anime packs mean by it.
        var parsed = _parser.Parse("/staging/[Group] Show Name - 123 (1080p).mkv");

        Assert.NotNull(parsed);
        Assert.Null(parsed!.SeasonNumber);
        Assert.Equal([123], parsed.AbsoluteNumbers);
        Assert.Empty(parsed.EpisodeNumbers);
    }

    [Fact]
    public void Parses_date_based()
    {
        var parsed = _parser.Parse("/staging/The.Daily.Show.2024.03.01.1080p.WEB.mkv");

        Assert.NotNull(parsed);
        Assert.Equal(new DateOnly(2024, 3, 1), parsed!.AirDate);
    }

    [Theory]
    [InlineData("/staging/Show.S01E01.sample.mkv")]
    [InlineData("/staging/Sample/Show.S01E01.mkv")]
    [InlineData("/staging/Show.S01/Extras/Show.S01E01.mkv")]
    [InlineData("/staging/Show.S01/Featurettes/Show.S01E02.mkv")]
    [InlineData("/staging/Show.S01/Behind the Scenes/Show.S01E03.mkv")]
    public void Ignores_a_sample_file(string path)
    {
        Assert.Null(_parser.Parse(path));
    }

    [Theory]
    [InlineData("/staging/Extraction.2020.1080p.S01E01.mkv")]
    [InlineData("/staging/Interstellar.S01E01.mkv")]
    public void A_title_that_merely_contains_an_excluded_word_is_still_imported(string path)
    {
        // The markers are whole tokens, never substrings: "Extraction" is not "extra".
        Assert.NotNull(_parser.Parse(path));
    }

    [Fact]
    public void A_staging_ancestor_is_never_an_extras_bucket()
    {
        // The extras markers describe folders inside the download. An admin whose staging mount is
        // /mnt/media-bonus would otherwise have every episode of every pack silently unresolved.
        const string content = "/mnt/media-bonus/incomplete/Show.S01.1080p.WEB-DL";

        var parsed = _parser.Parse($"{content}/Show.S01E01.mkv", content);

        Assert.NotNull(parsed);
        Assert.Equal(1, parsed!.SeasonNumber);
        Assert.Equal([1], parsed.EpisodeNumbers);
    }

    [Fact]
    public void An_extras_folder_inside_the_download_is_still_excluded()
    {
        const string content = "/mnt/media-bonus/incomplete/Show.S01.1080p.WEB-DL";

        Assert.Null(_parser.Parse($"{content}/Extras/Show.S01E02.mkv", content));
    }

    [Fact]
    public void An_episode_of_a_series_whose_title_carries_a_marker_is_still_read()
    {
        // "Trailer Park Boys" tokenises to [trailer, park, boys]; a whole-token match on the file
        // name would refuse every episode of the series for good.
        const string content = "/data/staging/Trailer.Park.Boys.S01.1080p.WEB-DL";

        Assert.NotNull(_parser.Parse($"{content}/Trailer.Park.Boys.S01E01.mkv", content));
    }

    [Fact]
    public void A_file_with_no_numbering_yields_nothing()
    {
        Assert.Null(_parser.Parse("/staging/readme.txt"));
        Assert.Null(_parser.Parse(string.Empty));
    }
}
