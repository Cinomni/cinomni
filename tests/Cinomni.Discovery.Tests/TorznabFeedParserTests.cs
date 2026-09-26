using Cinomni.Discovery.Contracts;
using Cinomni.Discovery.Indexers;

namespace Cinomni.Discovery.Tests;

/// <summary>Unit tests for the Torznab/Newznab feed parser — the error-prone part of the real transport.</summary>
public sealed class TorznabFeedParserTests
{
    private const string TwoItemFeed = """
        <?xml version="1.0" encoding="UTF-8"?>
        <rss version="2.0" xmlns:torznab="http://torznab.com/schemas/2015/feed">
          <channel>
            <item>
              <title>Interstellar 2014 1080p BluRay x264</title>
              <guid>https://idx.example/details/1</guid>
              <link>https://idx.example/download/1.torrent</link>
              <pubDate>Wed, 12 Nov 2014 10:00:00 +0000</pubDate>
              <enclosure url="magnet:?xt=urn:btih:ABC123" length="8589934592" type="application/x-bittorrent" />
              <torznab:attr name="seeders" value="120" />
              <torznab:attr name="size" value="8589934592" />
            </item>
            <item>
              <title>Interstellar 2014 720p WEB</title>
              <guid>https://idx.example/details/2</guid>
              <enclosure url="magnet:?xt=urn:btih:DEF456" length="4294967296" type="application/x-bittorrent" />
              <torznab:attr name="seeders" value="50" />
            </item>
          </channel>
        </rss>
        """;

    [Fact]
    public void Parses_titles_urls_size_seeders_and_protocol()
    {
        var candidates = TorznabFeedParser.Parse(TwoItemFeed, "TestIndexer", ReleaseProtocol.Torrent);

        Assert.Equal(2, candidates.Count);

        var first = candidates[0];
        Assert.Equal("Interstellar 2014 1080p BluRay x264", first.Title);
        Assert.Equal("https://idx.example/details/1", first.Guid);
        Assert.Equal("magnet:?xt=urn:btih:ABC123", first.DownloadUrl);
        Assert.Equal(8589934592L, first.SizeBytes);
        Assert.Equal(120, first.Seeders);
        Assert.Equal(ReleaseProtocol.Torrent, first.Protocol);
        Assert.Equal("TestIndexer", first.IndexerName);
        Assert.NotNull(first.PublishedAt);

        // Size falls back to the enclosure length when no torznab size attribute is present.
        Assert.Equal(4294967296L, candidates[1].SizeBytes);
    }

    [Fact]
    public void PublishedAt_is_normalized_to_utc_even_when_the_feed_carries_its_own_offset()
    {
        // Npgsql refuses to persist a DateTimeOffset whose Offset isn't zero into timestamptz — a
        // real indexer (e.g. one hosted in Australia) sends pubDate in its own local offset, and the
        // parser must convert it rather than merely accept it.
        const string feed = """
            <rss><channel>
              <item>
                <title>A Show S01E01</title>
                <guid>g1</guid>
                <link>https://idx.example/1</link>
                <pubDate>Fri, 17 Nov 2023 03:53:15 +1000</pubDate>
              </item>
            </channel></rss>
            """;

        var candidate = Assert.Single(TorznabFeedParser.Parse(feed, "Idx", ReleaseProtocol.Torrent));

        Assert.NotNull(candidate.PublishedAt);
        Assert.Equal(TimeSpan.Zero, candidate.PublishedAt!.Value.Offset);
        Assert.Equal(new DateTimeOffset(2023, 11, 16, 17, 53, 15, TimeSpan.Zero), candidate.PublishedAt);
    }

    [Fact]
    public void Skips_items_without_a_title()
    {
        const string feed = """
            <rss><channel>
              <item><guid>g1</guid><link>https://idx.example/1</link></item>
              <item><title>Has Title</title><link>https://idx.example/2</link></item>
            </channel></rss>
            """;

        var candidates = TorznabFeedParser.Parse(feed, "Idx", ReleaseProtocol.Torrent);

        Assert.Single(candidates);
        Assert.Equal("Has Title", candidates[0].Title);
    }

    [Fact]
    public void Malformed_xml_is_a_failed_indexer_not_an_empty_feed()
    {
        var failure = Assert.Throws<TorznabFeedException>(
            () => TorznabFeedParser.Parse("this is not <<< xml", "Idx", ReleaseProtocol.Torrent));

        Assert.Equal(TorznabFeedException.Unreadable, failure.Code);
    }

    [Fact]
    public void Rejects_dtd_so_external_entities_never_expand()
    {
        // A DOCTYPE with an external entity: the hardened reader prohibits DTDs, so this is refused as
        // unreadable instead of resolving file:/// (XXE defense).
        const string xxe = """
            <?xml version="1.0"?>
            <!DOCTYPE rss [<!ENTITY xxe SYSTEM "file:///etc/passwd">]>
            <rss><channel><item><title>&xxe;</title><link>https://idx.example/1</link></item></channel></rss>
            """;

        var failure = Assert.Throws<TorznabFeedException>(() => TorznabFeedParser.Parse(xxe, "Idx", ReleaseProtocol.Torrent));

        Assert.Equal(TorznabFeedException.Unreadable, failure.Code);
        Assert.DoesNotContain("passwd", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""<?xml version="1.0"?><error code="100" description="Incorrect user credentials"/>""", "100")]
    [InlineData("""<error code="429" description="Request limit reached"/>""", "429")]
    [InlineData("""<error description="no code; name='$(evil)'"/>""", TorznabFeedException.UnknownCode)]
    public void An_error_document_answered_with_200_is_a_failed_indexer(string xml, string code)
    {
        // The protocol answers errors with HTTP 200. Read as a feed, a wrong API key looked like a title
        // nobody seeds; the indexer's own description never reaches the message.
        var failure = Assert.Throws<TorznabFeedException>(() => TorznabFeedParser.Parse(xml, "Idx", ReleaseProtocol.Torrent));

        Assert.Equal(code, failure.Code);
        Assert.DoesNotContain("credentials", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("evil", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_feed_with_no_items_is_still_an_honest_empty_answer()
    {
        const string empty = """<rss version="2.0"><channel><title>Idx</title></channel></rss>""";

        Assert.Empty(TorznabFeedParser.Parse(empty, "Idx", ReleaseProtocol.Torrent));
    }

    [Fact]
    public void Reads_season_and_episode_attrs()
    {
        // The indexer's own attribution is a far more reliable signal than re-parsing the title.
        const string feed = """
            <rss version="2.0" xmlns:torznab="http://torznab.com/schemas/2015/feed"><channel>
              <item>
                <title>The.Wire.S02E05.1080p.WEB-DL</title>
                <guid>g1</guid>
                <enclosure url="magnet:?xt=urn:btih:ABC" length="1000" type="application/x-bittorrent" />
                <torznab:attr name="season" value="2" />
                <torznab:attr name="episode" value="5" />
                <torznab:attr name="category" value="5040" />
              </item>
            </channel></rss>
            """;

        var candidate = Assert.Single(TorznabFeedParser.Parse(feed, "Idx", ReleaseProtocol.Torrent));

        Assert.Equal(2, candidate.SeasonNumber);
        Assert.Equal(5, candidate.EpisodeNumber);
        Assert.Equal("5040", candidate.Category);
    }

    [Fact]
    public void Reads_tvdbid_attr()
    {
        // Newznab's attribute namespace is searched too, and "ep" is accepted where an endpoint
        // echoes the request parameter name instead of the documented "episode".
        const string feed = """
            <rss version="2.0" xmlns:newznab="http://www.newznab.com/DTD/2010/feeds/attributes/"><channel>
              <item>
                <title>The.Wire.S02E05.1080p.WEB-DL</title>
                <guid>g1</guid>
                <enclosure url="magnet:?xt=urn:btih:ABC" length="1000" type="application/x-bittorrent" />
                <newznab:attr name="tvdbid" value="79126" />
                <newznab:attr name="ep" value="5" />
              </item>
            </channel></rss>
            """;

        var candidate = Assert.Single(TorznabFeedParser.Parse(feed, "Idx", ReleaseProtocol.Torrent));

        Assert.Equal("79126", candidate.TvdbId);
        Assert.Equal(5, candidate.EpisodeNumber);
        Assert.Null(candidate.SeasonNumber);
    }

    [Fact]
    public void A_feed_without_numbering_attrs_leaves_them_null()
    {
        var candidates = TorznabFeedParser.Parse(TwoItemFeed, "Idx", ReleaseProtocol.Torrent);

        Assert.All(candidates, c =>
        {
            Assert.Null(c.SeasonNumber);
            Assert.Null(c.EpisodeNumber);
            Assert.Null(c.TvdbId);
            Assert.Null(c.Category);
        });
    }

    [Fact]
    public void Newznab_default_protocol_is_used_when_not_a_torrent()
    {
        const string feed = """
            <rss><channel>
              <item>
                <title>Some Usenet Release</title>
                <guid>g1</guid>
                <enclosure url="https://idx.example/nzb/1" length="1000" type="application/x-nzb" />
              </item>
            </channel></rss>
            """;

        var candidates = TorznabFeedParser.Parse(feed, "Idx", ReleaseProtocol.Usenet);

        Assert.Single(candidates);
        Assert.Equal(ReleaseProtocol.Usenet, candidates[0].Protocol);
    }

    [Fact]
    public void Prefer_magnet_selects_magnet_when_feed_also_has_an_enclosure()
    {
        const string feed = """
            <rss xmlns:torznab="http://torznab.com/schemas/2015/feed"><channel><item>
              <title>Sintel 1080p</title>
              <enclosure url="https://idx.example/download/1.torrent" type="application/x-bittorrent" />
              <torznab:attr name="magneturl" value="magnet:?xt=urn:btih:ABC" />
            </item></channel></rss>
            """;

        var candidate = Assert.Single(TorznabFeedParser.Parse(feed, "Idx", ReleaseProtocol.Torrent, preferMagnet: true));

        Assert.Equal("magnet:?xt=urn:btih:ABC", candidate.DownloadUrl);
    }
}
