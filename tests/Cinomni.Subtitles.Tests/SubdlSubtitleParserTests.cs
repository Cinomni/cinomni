using System.Text.Json;
using Cinomni.Subtitles.Providers;

namespace Cinomni.Subtitles.Tests;

public sealed class SubdlSubtitleParserTests
{
    [Fact]
    public void An_unpacked_file_for_the_asked_episode_is_kept_and_a_zip_is_not()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "status": true,
              "subtitles": [
                {
                  "release_name": "Season Pack",
                  "name": "Season.Pack.zip",
                  "url": "/subtitle/1-2.zip",
                  "unpack_files": [
                    {
                      "name": "Episode.One.srt",
                      "release_name": "Show.S01E01",
                      "season": 1,
                      "episode": 1,
                      "language": "EN",
                      "hi": true,
                      "format": "srt",
                      "url": "/subtitle/parent/file1"
                    },
                    {
                      "name": "Episode.Two.srt",
                      "season": 1,
                      "episode": 2,
                      "format": "srt",
                      "url": "/subtitle/parent/file2"
                    }
                  ]
                }
              ]
            }
            """);

        var query = new SubtitleProviderQuery("Show.S01E01", "en", HearingImpaired: true, SeasonNumber: 1, EpisodeNumber: 1);
        var candidates = SubdlSubtitleParser.Parse(document.RootElement, query);

        var only = Assert.Single(candidates);
        Assert.Equal("/subtitle/parent/file1", only.DownloadRef);
        Assert.True(only.HearingImpaired);
        Assert.False(only.Forced);
    }

    [Fact]
    public void A_file_named_forced_is_marked_and_a_lookalike_is_not()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "status": true,
              "subtitles": [
                {
                  "name": "Show.S01E01.forced.srt",
                  "release_name": "Show.S01E01",
                  "season": 1,
                  "episode": 1,
                  "format": "srt",
                  "url": "/subtitle/parent/forced"
                },
                {
                  "name": "Show.S01E01.unforced.srt",
                  "season": 1,
                  "episode": 1,
                  "format": "srt",
                  "url": "/subtitle/parent/plain"
                }
              ]
            }
            """);

        var query = new SubtitleProviderQuery("Show.S01E01", "en", HearingImpaired: false, SeasonNumber: 1, EpisodeNumber: 1);
        var candidates = SubdlSubtitleParser.Parse(document.RootElement, query);

        Assert.Equal(2, candidates.Count);
        Assert.True(candidates[0].Forced);
        Assert.False(candidates[1].Forced);
    }

    [Fact]
    public void A_download_reference_off_the_provider_host_is_refused()
    {
        Assert.Null(SubdlSubtitleParser.DownloadUri("https://evil.example/subtitle/1"));
        Assert.Null(SubdlSubtitleParser.DownloadUri("/subtitle/../secret"));
        Assert.NotNull(SubdlSubtitleParser.DownloadUri("/subtitle/parent/file1"));
    }

    [Fact]
    public void A_zip_payload_is_not_treated_as_a_subtitle()
    {
        Assert.False(SubdlSubtitleParser.IsSubtitleBytes([0x50, 0x4B, 0x03, 0x04, 0, 0, 0, 0]));
        Assert.True(SubdlSubtitleParser.IsSubtitleBytes("1\n00:00:01,000 --> 00:00:02,000\nHi\n"u8.ToArray()));
    }
}
