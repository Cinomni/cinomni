using Cinomni.ReleaseParsing.Contracts;
using Cinomni.ReleaseParsing.Parsing;

namespace Cinomni.ReleaseParsing.Tests;

/// <summary>
/// The parser battery: a table of real-shaped release names → expected structure, plus the
/// idempotency property and the explicit-rejection contract. This is the module's largest suite.
/// </summary>
public sealed class ReleaseParserTests
{
    private readonly ReleaseParser _parser = new();

    [Theory]
    // title, resolution, source, modifier, group, year
    [InlineData("The.Matrix.1999.1080p.BluRay.x264-SPARKS", QualityResolution.R1080p, QualitySource.Bluray, QualityModifier.None, "SPARKS", 1999)]
    [InlineData("Inception.2010.2160p.UHD.BluRay.x265-TERMINAL", QualityResolution.R2160p, QualitySource.Bluray, QualityModifier.None, "TERMINAL", 2010)]
    [InlineData("Dune.2021.1080p.WEB-DL.DDP5.1.Atmos.H.264-EVO", QualityResolution.R1080p, QualitySource.WebDl, QualityModifier.None, "EVO", 2021)]
    [InlineData("Tenet.2020.1080p.BluRay.REMUX.AVC.DTS-HD.MA.5.1-EPSiLON", QualityResolution.R1080p, QualitySource.Bluray, QualityModifier.Remux, "EPSiLON", 2020)]
    [InlineData("Interstellar.2014.720p.BluRay.x264-YIFY", QualityResolution.R720p, QualitySource.Bluray, QualityModifier.None, "YIFY", 2014)]
    [InlineData("Old.Movie.2019.HDTV.x264-GROUP", QualityResolution.Unknown, QualitySource.Hdtv, QualityModifier.None, "GROUP", 2019)]
    [InlineData("Some.Film.2018.720p.WEBRip.x264-ABC", QualityResolution.R720p, QualitySource.WebRip, QualityModifier.None, "ABC", 2018)]
    [InlineData("Classic.1955.DVDRip.XviD-NOGRP", QualityResolution.Unknown, QualitySource.Dvd, QualityModifier.None, "NOGRP", 1955)]
    [InlineData("New.Thing.2023.HDCAM.x264-CAMGRP", QualityResolution.Unknown, QualitySource.Cam, QualityModifier.None, "CAMGRP", 2023)]
    [InlineData("Movie.2021.WEB.x264-XYZ", QualityResolution.Unknown, QualitySource.WebDl, QualityModifier.None, "XYZ", 2021)]
    public void Parses_core_attributes(
        string title,
        QualityResolution resolution,
        QualitySource source,
        QualityModifier modifier,
        string? group,
        int? year)
    {
        var result = _parser.Parse(title);

        Assert.True(result.IsSuccess);
        var parsed = result.Value;
        Assert.Equal(resolution, parsed.Quality.Resolution);
        Assert.Equal(source, parsed.Quality.Source);
        Assert.Equal(modifier, parsed.Quality.Modifier);
        Assert.Equal(group, parsed.ReleaseGroup);
        Assert.Equal(year, parsed.Year);
    }

    [Theory]
    // A tracker prefixes the name with its own domain; the scene group at the end is still the group.
    [InlineData("[ Torrent911.com ] Ad.Astra.2019.1080p.BluRay.x264-SPARKS", "SPARKS")]
    [InlineData("[www.Torrenting.org] Inception.2010.1080p.BluRay.x264-YIFY", "YIFY")]
    [InlineData("[TGx] The.Matrix.1999.1080p.BluRay.x264-SPARKS", "SPARKS")]
    // ...and with no scene group at all, the site prefix is not a release group either.
    [InlineData("[ Torrent911.com ] Ad.Astra.2019.1080p.BluRay.x264", null)]
    public void A_site_prefix_never_wins_over_the_scene_release_group(string title, string? group)
    {
        // Only the anime convention puts the group first; a movie release name must keep reporting the
        // scene group, because custom formats and the canonical key both read it.
        var parsed = _parser.Parse(title).Value;

        Assert.Equal(group, parsed.ReleaseGroup);
        Assert.Equal(ReleaseType.Movie, parsed.ReleaseType);
    }

    [Fact]
    public void Uses_the_last_year_so_a_title_number_does_not_shadow_the_release_year()
    {
        var result = _parser.Parse("Blade.Runner.2049.2017.2160p.BluRay.x265-GRP");

        Assert.True(result.IsSuccess);
        Assert.Equal(2017, result.Value.Year);
        Assert.Equal("blade-runner-2049.2017.2160p.bluray", result.Value.Identity.CanonicalKey);
    }

    [Theory]
    [InlineData("Arrival.2016.PROPER.1080p.BluRay.x264-GRP", 2, false, false)]
    [InlineData("Sicario.2015.REPACK.1080p.BluRay.x264-GRP", 1, false, true)]
    [InlineData("Movie.2019.REAL.PROPER.1080p.WEB-DL-GRP", 2, true, false)]
    [InlineData("Movie.2020.1080p.BluRay.x264-GRP", 1, false, false)]
    public void Detects_revision(string title, int version, bool real, bool isRepack)
    {
        var parsed = _parser.Parse(title).Value;

        Assert.Equal(version, parsed.Revision.Version);
        Assert.Equal(real, parsed.Revision.Real);
        Assert.Equal(isRepack, parsed.Revision.IsRepack);
    }

    [Theory]
    [InlineData("The.Godfather.1972.REMASTERED.1080p.BluRay.x264-GRP", "Remastered")]
    [InlineData("Blade.Runner.1982.The.Final.Cut.1080p.BluRay-GRP", "Final Cut")]
    [InlineData("Aliens.1986.Extended.Cut.1080p.BluRay.x264-GRP", "Extended Cut")]
    [InlineData("Some.Movie.2010.1080p.BluRay.x264-GRP", null)]
    public void Detects_edition(string title, string? edition)
    {
        Assert.Equal(edition, _parser.Parse(title).Value.Edition);
    }

    [Theory]
    [InlineData("Amelie.2001.FRENCH.1080p.BluRay.x264-GRP", "French")]
    [InlineData("Parasite.2019.MULTI.1080p.BluRay.x264-GRP", "Multi")]
    [InlineData("Some.Movie.2020.1080p.BluRay.x264-GRP", "Unknown")]
    public void Detects_language(string title, string language)
    {
        Assert.Contains(language, _parser.Parse(title).Value.Languages);
    }

    [Theory]
    [InlineData("1917.1080p.BluRay.x264-GRP", "1917.1080p.bluray")]
    [InlineData("2012.2009.1080p.BluRay.x264-GRP", "2012.2009.1080p.bluray")]
    // Numeric-looking movie titles that the series-numbering detectors must never claim.
    [InlineData("Blade.Runner.2049.1080p.BluRay.x264-GRP", "blade-runner.2049.1080p.bluray")]
    [InlineData("Ocean's.11.1080p.BluRay.x264-GRP", "ocean-s-11.1080p.bluray")]
    [InlineData("Se7en.1995.1080p.BluRay.x264-GRP", "se7en.1995.1080p.bluray")]
    [InlineData("S.W.A.T.2003.1080p.BluRay.x264-GRP", "s-w-a-t.2003.1080p.bluray")]
    public void A_title_that_is_a_year_number_still_parses(string title, string expectedCanonicalKey)
    {
        var result = _parser.Parse(title);

        Assert.True(result.IsSuccess);
        Assert.Equal(expectedCanonicalKey, result.Value.Identity.CanonicalKey);
        Assert.Equal(ReleaseType.Movie, result.Value.ReleaseType);
        Assert.Null(result.Value.Numbering);
    }

    [Fact]
    public void A_title_word_is_not_mistaken_for_a_tag_when_there_is_no_year()
    {
        // "Real" is part of the title, not a REAL scene tag; the tag scope starts at "1080p".
        var parsed = _parser.Parse("Real.Steel.1080p.BluRay-GRP").Value;

        Assert.False(parsed.Revision.Real);
        Assert.Equal("real-steel.1080p.bluray", parsed.Identity.CanonicalKey);
    }

    [Fact]
    public void Distinct_editions_get_distinct_canonical_keys()
    {
        var theatrical = _parser.Parse("Blade.Runner.1982.Theatrical.1080p.BluRay-GRP").Value;
        var directorsCut = _parser.Parse("Blade.Runner.1982.Directors.Cut.1080p.BluRay-GRP").Value;

        Assert.NotEqual(theatrical.Identity.CanonicalKey, directorsCut.Identity.CanonicalKey);
    }

    [Fact]
    public void Diacritics_are_folded_so_the_canonical_key_collapses_across_indexers()
    {
        var accented = _parser.Parse("Amélie.2001.1080p.BluRay-GRP").Value;
        var plain = _parser.Parse("Amelie.2001.1080p.BluRay-GRP").Value;

        Assert.Equal("amelie.2001.1080p.bluray", accented.Identity.CanonicalKey);
        Assert.Equal(accented.Identity.CanonicalKey, plain.Identity.CanonicalKey);
    }

    [Fact]
    public void A_movie_with_no_year_still_parses_with_a_best_effort_title()
    {
        var result = _parser.Parse("Some.Random.Release.1080p.BluRay.x264-GRP");

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value.Year);
        Assert.Equal("some-random-release.1080p.bluray", result.Value.Identity.CanonicalKey);
        Assert.Equal(QualitySource.Bluray, result.Value.Quality.Source);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("....")]
    public void Rejects_an_unparseable_title(string title)
    {
        var result = _parser.Parse(title);

        Assert.True(result.IsFailure);
        Assert.Equal("parsing.unable_to_parse", result.Error.Code);
    }

    [Theory]
    [InlineData("The.Matrix.1999.1080p.BluRay.x264-SPARKS")]
    [InlineData("Tenet.2020.1080p.BluRay.REMUX.AVC-EPSiLON")]
    [InlineData("Amelie.2001.FRENCH.720p.WEBRip-GRP")]
    public void Parsing_is_idempotent(string title)
    {
        var first = _parser.Parse(title).Value;
        var second = _parser.Parse(title).Value;

        Assert.Equal(first.Identity.CanonicalKey, second.Identity.CanonicalKey);
        Assert.Equal(first.Quality, second.Quality);
        Assert.Equal(first.Revision, second.Revision);
        Assert.Equal(first.Languages, second.Languages);
        Assert.Equal(first.ReleaseGroup, second.ReleaseGroup);
        Assert.Equal(first.Edition, second.Edition);
    }

    [Fact]
    public void A_pathological_title_returns_promptly_without_hanging()
    {
        // Linear patterns + a match timeout: a huge title must never hang the parse.
        var result = _parser.Parse(new string('a', 200_000) + ".2020.1080p.BluRay-GRP");

        // Either outcome is acceptable; the point is that it returns.
        Assert.True(result.IsSuccess || result.IsFailure);

        // A repeated-episode list is the nested quantifier the ReDoS budget is about: the list is
        // bounded to 20 entries, so it costs the same as a well-formed one.
        var repeated = _parser.Parse(
            "Show.S01" + string.Concat(Enumerable.Repeat("E01", 300)) + ".1080p.WEB-GRP");

        Assert.True(repeated.IsSuccess);
        Assert.Equal([1], repeated.Value.Numbering!.Episodes);

        // An absurd range is rejected by the span cap and degrades to the episode it starts at,
        // rather than expanding into ten thousand numbers.
        var absurdRange = _parser.Parse("Show.S01E01-E9999.1080p.WEB-GRP");

        Assert.True(absurdRange.IsSuccess);
        Assert.Equal([1], absurdRange.Value.Numbering!.Episodes);
    }

    [Fact]
    public void Records_the_parser_version()
    {
        Assert.Equal(ReleaseParser.Version, _parser.Parse("Movie.2020.1080p.BluRay-GRP").Value.ParserVersion);
    }
}
