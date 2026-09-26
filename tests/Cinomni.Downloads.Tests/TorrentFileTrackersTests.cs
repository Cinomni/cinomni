using System.Text;
using Cinomni.Downloads.Engine;

namespace Cinomni.Downloads.Tests;

/// <summary>
/// The .torrent handed to an external qBittorrent announces only to trackers, and fetches only from
/// web seeds, that pass the magnet tracker rule — without touching the info dictionary its hash is
/// computed from.
/// </summary>
public sealed class TorrentFileTrackersTests
{
    private const string Info = "d6:lengthi1024e4:name9:Movie.mkv12:piece lengthi16384e6:pieces20:aaaaaaaaaaaaaaaaaaaae";

    [Fact]
    public void Internal_trackers_and_web_seeds_are_removed_and_the_info_hash_is_kept()
    {
        // Arrange
        var original = Torrent(
            Entry("announce", Str("http://192.168.1.1/announce"))
            + Entry("announce-list", "l"
                + List(Str("udp://tracker.example:6969/announce"), Str("http://127.0.0.1/announce"))
                + List(Str("http://10.0.0.5/announce"))
                + List(Str("udp://tracker.example:6969?a:@192.168.1.1"))
                + "e")
            + Entry("comment", Str("kept as is"))
            + Entry("httpseeds", List(Str("https://seed.example/")))
            + Entry("info", Info)
            + Entry("url-list", List(Str("https://mirror.example/files/"), Str("http://169.254.169.254/latest"), Str("udp://mirror.example/"))));

        // Act
        var filtered = TorrentFileTrackers.Filter(original);

        // Assert
        Assert.NotNull(filtered);
        var text = Encoding.UTF8.GetString(filtered!);
        Assert.Equal(
            Encoding.UTF8.GetString(Torrent(
                Entry("announce-list", List(List(Str("udp://tracker.example:6969/announce"))))
                + Entry("comment", Str("kept as is"))
                + Entry("info", Info)
                + Entry("url-list", List(Str("https://mirror.example/files/"))))),
            text);
        Assert.Equal(TorrentInfoHash.FromTorrent(original), TorrentInfoHash.FromTorrent(filtered));
    }

    [Fact]
    public void A_single_web_seed_given_as_a_string_is_kept_when_public()
    {
        var filtered = TorrentFileTrackers.Filter(Torrent(
            Entry("announce", Str("https://tracker.example/announce"))
            + Entry("info", Info)
            + Entry("url-list", Str("https://mirror.example/Movie.mkv"))));

        Assert.Equal(
            Encoding.UTF8.GetString(Torrent(
                Entry("announce", Str("https://tracker.example/announce"))
                + Entry("info", Info)
                + Entry("url-list", List(Str("https://mirror.example/Movie.mkv"))))),
            Encoding.UTF8.GetString(filtered!));
    }

    [Fact]
    public void No_more_trackers_than_a_magnet_may_carry_are_kept()
    {
        var tiers = string.Concat(Enumerable.Range(0, 30).Select(i => List(Str($"udp://t{i}.example:6969/announce"))));

        var filtered = Encoding.UTF8.GetString(TorrentFileTrackers.Filter(Torrent(
            Entry("announce-list", "l" + tiers + "e") + Entry("info", Info)))!);

        Assert.Equal(MagnetLink.MaxTrackers, filtered.Split("udp://").Length - 1);
    }

    [Fact]
    public void Dht_nodes_duplicate_keys_and_trailing_bytes_cannot_carry_a_destination()
    {
        // Arrange — "nodes" names hosts the client's DHT pings; a second "announce" key hopes the
        // filter reads only the first; "announce" inside info is part of the hash; and bytes after
        // the dictionary are not part of it.
        var info = "d8:announce22:http://10.0.0.1/secret6:lengthi1e4:name1:x12:piece lengthi16384e6:pieces20:aaaaaaaaaaaaaaaaaaaae";
        var original = Torrent(
            Entry("announce", Str("https://tracker.example/announce"))
            + Entry("announce", Str("http://192.168.0.1/announce"))
            + Entry("info", info)
            + Entry("nodes", List(List(Str("192.168.0.1"), "i6881e"))))
            .Concat(Encoding.UTF8.GetBytes("8:trailing")).ToArray();

        // Act
        var filtered = TorrentFileTrackers.Filter(original);

        // Assert
        Assert.Equal(
            Encoding.UTF8.GetString(Torrent(Entry("announce", Str("https://tracker.example/announce")) + Entry("info", info))),
            Encoding.UTF8.GetString(filtered!));
        Assert.Equal(TorrentInfoHash.FromTorrent(original), TorrentInfoHash.FromTorrent(filtered));
    }

    [Fact]
    public void Web_seeds_are_capped_like_trackers()
    {
        var seeds = Enumerable.Range(0, 30).Select(i => Str($"https://mirror{i}.example/x")).ToArray();

        var filtered = Encoding.UTF8.GetString(TorrentFileTrackers.Filter(Torrent(
            Entry("info", Info) + Entry("url-list", List(seeds))))!);

        Assert.Equal(MagnetLink.MaxTrackers, filtered.Split("https://").Length - 1);
    }

    [Fact]
    public void A_payload_nested_deeper_than_the_reader_allows_is_refused()
    {
        var deep = new string('l', 40) + new string('e', 40);

        Assert.Null(TorrentFileTrackers.Filter(Torrent(Entry("comment", deep) + Entry("info", Info))));
    }

    [Theory]
    [InlineData("")]
    [InlineData("le")]
    [InlineData("d8:announce")]
    [InlineData("d8:announce5:httpe")]
    [InlineData("d4:info" + Info)]
    public void A_payload_that_is_not_a_whole_dictionary_is_refused(string payload) =>
        Assert.Null(TorrentFileTrackers.Filter(Encoding.UTF8.GetBytes(payload)));

    private static byte[] Torrent(string entries) => Encoding.UTF8.GetBytes("d" + entries + "e");

    private static string Entry(string key, string value) => Str(key) + value;

    private static string Str(string value) => $"{Encoding.UTF8.GetByteCount(value)}:{value}";

    private static string List(params string[] items) => "l" + string.Concat(items) + "e";
}
