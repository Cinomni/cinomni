using Cinomni.Playback.Application;

namespace Cinomni.Playback.Tests;

/// <summary>
/// Safari plays HLS itself and fetches each segment by the relative name in the playlist, without the
/// query the playlist came with; every segment was refused. The playlist now hands the token on.
/// </summary>
public sealed class HlsPlaylistTests
{
    private const string Playlist =
        "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:6\n#EXTINF:6.000000,\nseg_000.ts\n#EXTINF:6.000000,\nseg_001.ts\n";

    [Fact]
    public void Every_segment_carries_the_token_the_playlist_was_fetched_with()
    {
        var rewritten = HlsPlaylist.CarryQueryToken(Playlist, "tok+en/=");

        Assert.Contains("seg_000.ts?access_token=tok%2Ben%2F%3D\n", rewritten, StringComparison.Ordinal);
        Assert.Contains("seg_001.ts?access_token=tok%2Ben%2F%3D\n", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void Tags_and_anything_that_is_not_a_bare_segment_name_are_left_alone()
    {
        const string playlist = "#EXTM3U\n#EXT-X-MAP:URI=\"init.ts\"\nhttps://elsewhere.example/seg.ts\n\nseg_002.ts\r\n";

        var rewritten = HlsPlaylist.CarryQueryToken(playlist, "t");

        Assert.Equal(
            "#EXTM3U\n#EXT-X-MAP:URI=\"init.ts\"\nhttps://elsewhere.example/seg.ts\n\nseg_002.ts?access_token=t\n",
            rewritten);
    }

    [Fact]
    public void A_fragmented_mp4_playlist_carries_the_token_on_its_init_segment_and_every_fragment()
    {
        // HEVC in a browser is fragmented MP4: the init segment is named inside a tag, not on a line of its own.
        const string playlist = "#EXTM3U\n#EXT-X-MAP:URI=\"init.mp4\"\n#EXTINF:6.0,\nseg_000.m4s\n";

        var rewritten = HlsPlaylist.CarryQueryToken(playlist, "t");

        Assert.Equal(
            "#EXTM3U\n#EXT-X-MAP:URI=\"init.mp4?access_token=t\"\n#EXTINF:6.0,\nseg_000.m4s?access_token=t\n",
            rewritten);
    }
}
