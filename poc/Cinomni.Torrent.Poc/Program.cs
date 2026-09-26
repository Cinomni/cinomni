using Grpc.Core;
using Grpc.Net.Client;
using Cinomni.Torrent.Grpc;

// The sidecar speaks HTTP/2 cleartext (h2c); allow it for this local PoC.
AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

var address = args.Length > 0 ? args[0] : "http://localhost:50051";
const int TestSizeBytes = 4 * 1024 * 1024;

using var channel = GrpcChannel.ForAddress(address);
var client = new TorrentService.TorrentServiceClient(channel);

Console.WriteLine($"== PoC sidecar libtorrent @ {address} ==");

// 1. Seed a locally-generated test torrent (no internet, no third-party content).
var test = await client.CreateTestTorrentAsync(new CreateTestTorrentRequest
{
    SizeBytes = TestSizeBytes,
    Name = "poc.bin",
});
Console.WriteLine($"1) test torrent seeded: infoHash={test.InfoHash} seederPort={test.SeederPort} " +
                  $"(.torrent {test.TorrentFile.Length} bytes)");

// 2. Add it as a download, pointed at the local seeder.
var add = await client.AddTorrentAsync(new AddTorrentRequest
{
    TorrentFile = test.TorrentFile,
    SavePath = "/data/dl",
    Peers = { new PeerEndpoint { Host = "127.0.0.1", Port = test.SeederPort } },
});
Console.WriteLine($"2) added download: infoHash={add.InfoHash} name={add.Name}");

// 3. Stream progress until the download completes.
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
using var stream = client.StreamStatus(new InfoHashRequest { InfoHash = add.InfoHash });
TorrentStatus? last = null;
await foreach (var status in stream.ResponseStream.ReadAllAsync(cts.Token))
{
    Console.WriteLine($"   [{status.State,-22}] {status.Progress,6:P1} " +
                      $"done={status.TotalDone}/{status.TotalWanted} " +
                      $"peers={status.NumPeers} seeds={status.NumSeeds} down={status.DownloadRate}B/s");
    last = status;
    if (status.IsFinished)
    {
        break;
    }
}
Console.WriteLine($"3) complete: finished={last?.IsFinished}");

// 4. Persist resume data (the .fastresume checkpoint).
var resume = await client.SaveResumeDataAsync(new InfoHashRequest { InfoHash = add.InfoHash });
Console.WriteLine($"4) resume data saved: {resume.Data.Length} bytes");

// 5. Restart: remove (keep files) then re-add from resume data — should resume without a
//    full recheck because the data on disk matches.
await client.RemoveTorrentAsync(new RemoveTorrentRequest { InfoHash = add.InfoHash, DeleteFiles = false });
var readded = await client.AddTorrentAsync(new AddTorrentRequest { ResumeData = resume.Data });
Console.WriteLine($"5) re-added from resume: infoHash={readded.InfoHash} resumed={readded.Resumed}");

await Task.Delay(TimeSpan.FromSeconds(2));
var after = await client.GetStatusAsync(new InfoHashRequest { InfoHash = readded.InfoHash });
Console.WriteLine($"   after resume: [{after.State}] {after.Progress:P1} finished={after.IsFinished}");

var ok = (last?.IsFinished ?? false) && resume.Data.Length > 0 && after.IsFinished;
Console.WriteLine(ok ? "== PoC OK ==" : "== PoC FAILED ==");
return ok ? 0 : 1;
