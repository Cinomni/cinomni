using System.Security.Cryptography;
using System.Text;
using Cinomni.Downloads.Engine;

namespace Cinomni.Downloads.Tests;

public sealed class QbittorrentTests
{
    [Fact]
    public void A_magnet_hash_is_the_btih_in_lowercase_hex()
    {
        var hash = new string('A', 40);
        var parsed = TorrentInfoHash.FromMagnet($"magnet:?xt=urn:btih:{hash}&dn=Name");

        Assert.Equal(hash.ToLowerInvariant(), parsed);
    }

    [Fact]
    public void A_torrent_payload_hashes_the_info_dictionary_and_nothing_else()
    {
        var info = "d6:lengthi1e4:name4:teste"u8.ToArray();
        var payload = Encoding.ASCII.GetBytes("d4:info" + Encoding.ASCII.GetString(info) + "e");

        var parsed = TorrentInfoHash.FromTorrent(payload);

        Assert.Equal(Convert.ToHexString(SHA1.HashData(info)).ToLowerInvariant(), parsed);
    }

    [Fact]
    public void A_hostile_payload_does_not_yield_a_hash()
    {
        Assert.Null(TorrentInfoHash.FromTorrent("<html>"u8));
        Assert.Null(TorrentInfoHash.FromMagnet("https://example.invalid/file.torrent"));
    }

    [Fact]
    public void Metadata_and_seeding_states_use_the_engine_vocabulary()
    {
        Assert.Equal("downloading_metadata", QbittorrentState.Map("metaDL", 0).State);
        Assert.Equal("seeding", QbittorrentState.Map("uploading", 1).State);
        Assert.True(QbittorrentState.Map("pausedDL", 0.2).IsPaused);
    }

    [Fact]
    public void The_clients_own_queue_is_waiting_and_its_newer_stopped_states_are_paused()
    {
        // qBittorrent keeps only a few downloads active; the rest wait in queuedDL and must not stall.
        var queued = QbittorrentState.Map("queuedDL", 0.1);
        Assert.True(queued.IsQueued);
        Assert.False(queued.IsPaused);

        Assert.True(QbittorrentState.Map("stoppedDL", 0.2).IsPaused);
        var stoppedUp = QbittorrentState.Map("stoppedUP", 1);
        Assert.True(stoppedUp.IsPaused);
        Assert.True(stoppedUp.IsFinished);
        Assert.False(QbittorrentState.Map("downloading", 0.2).IsQueued);
        Assert.Equal("qbittorrent-error", QbittorrentState.Map("error", 0).Error);
    }
}
