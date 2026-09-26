using Cinomni.Import.Application;
using Cinomni.ReleaseParsing.Parsing;

namespace Cinomni.Import.Tests;

/// <summary>
/// Reading the quality off the name a file arrived under — the only moment it is still legible.
/// </summary>
public sealed class ReleaseQualityReaderTests
{
    private readonly ReleaseQualityReader _reader = new(new ReleaseParser());

    [Fact]
    public void Reads_the_source_and_resolution_from_the_file_name()
    {
        var quality = _reader.Read("/downloads/The.Matrix.1999.1080p.BluRay.x264-GRP.mkv", contentPath: null);

        Assert.NotNull(quality);
        Assert.Equal("Bluray", quality.Source);
        Assert.Equal("R1080p", quality.Resolution);
    }

    [Fact]
    public void Tells_a_web_dl_apart_from_a_bluray_of_the_same_resolution()
    {
        // The distinction the whole upgrade path turns on, and the one ffprobe cannot make.
        var bluray = _reader.Read("/d/Movie.2020.1080p.BluRay.x264-GRP.mkv", null);
        var web = _reader.Read("/d/Movie.2020.1080p.WEB-DL.x264-GRP.mkv", null);

        Assert.Equal("Bluray", bluray?.Source);
        Assert.Equal("WebDl", web?.Source);
        Assert.Equal(bluray?.Resolution, web?.Resolution);
    }

    [Fact]
    public void Falls_back_to_the_download_folder_when_the_episode_file_is_named_bare()
    {
        // How season packs routinely arrive: the quality is stated once, on the pack.
        var quality = _reader.Read(
            "/downloads/The.Wire.S01.1080p.BluRay.x264-GRP/The.Wire - S01E03.mkv",
            contentPath: "/downloads/The.Wire.S01.1080p.BluRay.x264-GRP");

        Assert.Equal("Bluray", quality?.Source);
        Assert.Equal("R1080p", quality?.Resolution);
    }

    [Fact]
    public void Prefers_the_file_over_the_folder_when_both_say_something()
    {
        // A mixed pack: the file knows better than the folder what the file is.
        var quality = _reader.Read(
            "/downloads/Show.S01.1080p.WEB-DL-GRP/Show.S01E03.2160p.BluRay-GRP.mkv",
            contentPath: "/downloads/Show.S01.1080p.WEB-DL-GRP");

        Assert.Equal("Bluray", quality?.Source);
        Assert.Equal("R2160p", quality?.Resolution);
    }

    [Fact]
    public void Says_nothing_rather_than_guessing_when_neither_name_carries_a_source()
    {
        // Unknown must stay unknown: a version recorded as "no source" would later be compared as if it
        // were the worst possible release, and replaced on the strength of a guess.
        Assert.Null(_reader.Read("/downloads/episode3.mkv", contentPath: "/downloads"));
    }

    [Fact]
    public void Does_not_read_a_quality_out_of_the_wider_path()
    {
        // "1080p" in a parent directory says nothing about this file.
        Assert.Null(_reader.Read("/media/1080p-stuff/episode3.mkv", contentPath: null));
    }
}
