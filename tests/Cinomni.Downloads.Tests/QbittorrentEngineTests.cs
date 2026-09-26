using System.Net;
using System.Text;
using Cinomni.Downloads.Contracts;
using Cinomni.Downloads.Engine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cinomni.Downloads.Tests;

/// <summary>
/// The qBittorrent engine against a scripted Web API: what it actually sends a client that splits the
/// "urls" it is given on line breaks, and which torrents already in that client it is willing to call
/// its own. Plus the rebuilt magnet and the bencode reader on hostile input.
/// </summary>
public sealed class QbittorrentEngineTests
{
    private const string Hash = "0123456789abcdef0123456789abcdef01234567";
    private const string Staging = "/data/downloads";

    [Fact]
    public async Task A_feed_cannot_slip_a_second_url_into_what_the_client_is_sent()
    {
        var api = new ScriptedApi();
        var engine = Engine(api);

        // "&#10;" in a feed arrives as a real line break once the XML is read.
        await engine.AddAsync(Request($"magnet:?xt=urn:btih:{Hash}&dn=Film\nhttp://192.168.1.1/admin&tr=udp%3A%2F%2Ftracker.example%3A1337"));

        var sent = Assert.Single(api.AddedUrls);
        Assert.DoesNotContain('\n', sent);
        Assert.DoesNotContain('\r', sent);
        Assert.StartsWith($"magnet:?xt=urn:btih:{Hash}", sent, StringComparison.Ordinal);
        Assert.Contains("&tr=udp%3A%2F%2Ftracker.example%3A1337", sent, StringComparison.Ordinal);
        // What followed the line break survives only as escaped text inside the name, never as a URL.
        Assert.DoesNotContain("http://", sent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_client_is_told_to_keep_the_save_path_it_is_given()
    {
        var api = new ScriptedApi();

        await Engine(api).AddAsync(Request($"magnet:?xt=urn:btih:{Hash}"));

        // Automatic torrent management would let the client choose its own directory instead.
        Assert.Equal("false", api.AddedFields["autoTMM"]);
        Assert.Equal(Staging, api.AddedFields["savepath"]);
    }

    [Theory]
    [InlineData("/data/downloads/")]
    [InlineData("/data//downloads")]
    [InlineData("/data/./downloads/")]
    [InlineData("/data/other/../downloads")]
    [InlineData("\\data\\downloads")]
    public async Task The_staging_area_is_recognised_however_the_client_spells_it(string reported)
    {
        var api = new ScriptedApi { Existing = ("Film", reported) };

        var added = await Engine(api).AddAsync(Request($"magnet:?xt=urn:btih:{Hash}"));

        Assert.True(added.Resumed);
    }

    [Fact]
    public async Task A_torrent_the_household_added_itself_is_left_alone()
    {
        var api = new ScriptedApi { Existing = ("Their Film", "/home/someone/Downloads") };
        var engine = Engine(api);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.AddAsync(Request($"magnet:?xt=urn:btih:{Hash}")));

        Assert.Empty(api.AddedUrls);
    }

    [Fact]
    public async Task A_torrent_already_saving_into_the_staging_area_is_picked_up_again()
    {
        var api = new ScriptedApi { Existing = ("Film", Staging + "/") };
        var engine = Engine(api);

        var added = await engine.AddAsync(Request($"magnet:?xt=urn:btih:{Hash}"));

        Assert.True(added.Resumed);
        Assert.Equal(Hash, added.InfoHash);
    }

    [Theory]
    [InlineData("metaDL", "", "")]
    [InlineData("forcedMetaDL", "", "")]
    [InlineData("downloading", ",\"has_metadata\":false", "")]
    [InlineData("stoppedDL", ",\"total_size\":0", "")]
    [InlineData("downloading", ",\"has_metadata\":true,\"total_size\":5", "Film")]
    [InlineData("downloading", "", "Film")]
    public async Task A_torrent_has_no_name_until_its_metadata_is_known(string state, string extraFields, string expected)
    {
        // Before the metadata the client reports the magnet's dn= as the name, and Downloads builds
        // the folder Import scans from the first name it is given.
        var api = new ScriptedApi { Existing = ("Film", Staging), State = state, ExtraFields = extraFields };

        var status = await Engine(api).GetStatusAsync(Hash);

        Assert.Equal(expected, status!.Name);
    }

    [Fact]
    public async Task The_client_is_reachable_only_when_it_answers_a_signed_in_read()
    {
        Assert.True(await Engine(new ScriptedApi()).IsReachableAsync(TimeSpan.FromSeconds(5)));
        Assert.False(await Engine(new ScriptedApi { LoginSucceeds = false }).IsReachableAsync(TimeSpan.FromSeconds(5)));
        Assert.False(await Engine(new ScriptedApi { VersionStatus = HttpStatusCode.ServiceUnavailable })
            .IsReachableAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Engine_instances_share_one_sign_in()
    {
        // The engine is a typed client, made anew per scope and per readiness probe.
        var api = new ScriptedApi();
        var session = new QbittorrentSession();

        Assert.True(await Engine(api, session).IsReachableAsync(TimeSpan.FromSeconds(5)));
        Assert.True(await Engine(api, session).IsReachableAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(1, api.Logins);
    }

    [Fact]
    public async Task A_refused_sign_in_is_not_retried_until_its_hold_off_ends()
    {
        // qBittorrent bans an address after a few failed sign-ins; retrying a wrong password on every
        // readiness probe would keep that ban in place.
        var clock = new ManualClock();
        var api = new ScriptedApi { LoginSucceeds = false };
        var session = new QbittorrentSession(clock);

        Assert.False(await Engine(api, session).IsReachableAsync(TimeSpan.FromSeconds(5)));
        Assert.False(await Engine(api, session).IsReachableAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, api.Logins);

        clock.Advance(QbittorrentSession.RefusalHoldOff);
        Assert.False(await Engine(api, session).IsReachableAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(2, api.Logins);
    }

    [Fact]
    public async Task A_client_that_never_answers_is_unreachable_within_the_timeout()
    {
        var engine = new QbittorrentWebEngine(
            new HttpClient(new NeverAnswers()) { BaseAddress = new Uri("http://qbittorrent:8080/api/v2/") },
            new NoHttpClients(),
            new QbittorrentOptions { BaseAddress = "http://qbittorrent:8080" },
            new QbittorrentSession(),
            new TunnelOptions(),
            NullLogger<QbittorrentWebEngine>.Instance);

        Assert.False(await engine.IsReachableAsync(TimeSpan.FromMilliseconds(200)));
    }

    [Theory]
    [InlineData(Staging + "/0199aaaa-0000-7000-8000-000000000001", true)]
    [InlineData(Staging + "/0199aaaa-0000-7000-8000-000000000001/", true)]
    [InlineData(Staging, true)]
    [InlineData(Staging + "-elsewhere/0199aaaa", false)]
    [InlineData("/home/someone/Downloads", false)]
    public async Task A_torrent_already_in_a_task_folder_inside_staging_is_adopted(string savedTo, bool adopted)
    {
        // Each task downloads into a folder of its own, so a torrent Cinomni added saves inside the
        // staging area rather than into it; a lookalike sibling of the area is still someone else's.
        var api = new ScriptedApi { Existing = ("Film", savedTo) };
        var request = new TorrentAddRequest(
            $"magnet:?xt=urn:btih:{Hash}", Staging + "/0199bbbb-0000-7000-8000-000000000002", null, Staging);

        if (adopted)
        {
            Assert.True((await Engine(api).AddAsync(request)).Resumed);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => Engine(api).AddAsync(request));
        }
    }

    [Fact]
    public async Task A_staging_root_of_slash_adopts_nothing()
    {
        // "/" contains every path; adopting on that would hand Cinomni every torrent the household has.
        var api = new ScriptedApi { Existing = ("Their Film", "/home/someone/Downloads") };
        var request = new TorrentAddRequest($"magnet:?xt=urn:btih:{Hash}", "/0199bbbb", null, "/");

        await Assert.ThrowsAsync<InvalidOperationException>(() => Engine(api).AddAsync(request));
    }

    [Fact]
    public async Task The_fetch_of_a_torrent_file_stops_at_the_sidecars_ceiling()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddQbittorrentEngine(options => options.BaseAddress = "http://qbittorrent:8080");
        // The real client, with only the network under it replaced by an indexer that answers 9 MiB.
        services.AddHttpClient(QbittorrentWebEngine.TorrentFetchClient)
            .ConfigurePrimaryHttpMessageHandler(() => new OversizedIndexer(9 * 1024 * 1024));
        using var provider = services.BuildServiceProvider();

        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(QbittorrentWebEngine.TorrentFetchClient);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://indexer.example/huge.torrent"));
    }

    [Fact]
    public void A_rebuilt_magnet_keeps_what_it_means_and_nothing_smuggled()
    {
        var link = MagnetLink.Canonical(
            $"magnet:?xt=urn:btih:{Hash.ToUpperInvariant()}&dn=Film%0A%07Name"
            + "&tr=http%3A%2F%2Ftracker.example%2Fannounce"
            + "&tr=udp%3A%2F%2F127.0.0.1%3A6969"
            + "&tr=http%3A%2F%2Fuser%3Apass%40tracker.example%2F"
            + "&tr=http%3A%2F%2Flocalhost%2Fannounce"
            + "&tr=file%3A%2F%2F%2Fetc%2Fpasswd"
            + "&tr.1=https%3A%2F%2Fother.example%2Fannounce");

        Assert.Equal(
            $"magnet:?xt=urn:btih:{Hash}&dn=FilmName"
            + "&tr=http%3A%2F%2Ftracker.example%2Fannounce&tr=https%3A%2F%2Fother.example%2Fannounce",
            link);
    }

    [Theory]
    // Where .NET and the torrent engine disagree: .NET sees a query or a fragment on a public host, the
    // engine sees credentials up to the "@" and announces to the address after it.
    [InlineData("udp://tracker.example:6969?a:@192.168.1.1:6969")]
    [InlineData("http://tracker.example#:@169.254.169.254/latest/meta-data")]
    [InlineData("http://tracker.example\\@10.0.0.1/announce")]
    // Address literals in every spelling that still means this machine or its network.
    [InlineData("http://[::1]/announce")]
    [InlineData("http://[::ffff:127.0.0.1]/announce")]
    [InlineData("http://2130706433/announce")]
    [InlineData("http://0x7f000001/announce")]
    [InlineData("udp://0.0.0.0:6969")]
    [InlineData("udp://[fe80::1]:6969")]
    public void An_announce_url_that_could_reach_inside_is_dropped(string tracker)
    {
        Assert.False(MagnetLink.IsAcceptableTracker(tracker));
    }

    [Fact]
    public void A_name_keeps_its_spaces_and_loses_any_path()
    {
        var link = MagnetLink.Canonical($"magnet:?xt=urn:btih:{Hash}&dn=The+Film+..%2F..%2Fetc");

        Assert.Equal($"magnet:?xt=urn:btih:{Hash}&dn=The%20Film%20.._.._etc", link);
    }

    [Fact]
    public void A_rebuilt_magnet_keeps_at_most_a_bounded_number_of_trackers()
    {
        var trackers = string.Concat(Enumerable.Range(0, 50).Select(i => $"&tr=udp%3A%2F%2Ft{i}.example%3A80"));

        var link = MagnetLink.Canonical($"magnet:?xt=urn:btih:{Hash}{trackers}");

        Assert.Equal(MagnetLink.MaxTrackers, link!.Split("&tr=").Length - 1);
    }

    [Fact]
    public void Only_a_magnet_with_an_info_hash_is_rebuilt()
    {
        Assert.Null(MagnetLink.Canonical("https://indexer.example/file.torrent"));
        Assert.Null(MagnetLink.Canonical("magnet:?dn=No+hash"));
    }

    [Fact]
    public void A_declared_length_that_would_overflow_is_refused_rather_than_thrown()
    {
        // A string length near int.MaxValue used to wrap "start + length" negative, pass the bound and
        // throw from Slice — against the reader's promise to answer null for anything hostile.
        var payload = Encoding.ASCII.GetBytes("d4:infod4:name2147483647:abcee");

        Assert.Null(TorrentInfoHash.FromTorrent(payload));
    }

    private static TorrentAddRequest Request(string url) => new(url, Staging, ResumeData: null);

    private static QbittorrentWebEngine Engine(ScriptedApi api, QbittorrentSession? session = null) => new(
        new HttpClient(api) { BaseAddress = new Uri("http://qbittorrent:8080/api/v2/") },
        new NoHttpClients(),
        new QbittorrentOptions { BaseAddress = "http://qbittorrent:8080" },
        session ?? new QbittorrentSession(),
        new TunnelOptions(),
        NullLogger<QbittorrentWebEngine>.Instance);

    /// <summary>Just enough of the Web API: a login, one torrent list, and an add that records what it got.</summary>
    private sealed class ScriptedApi : HttpMessageHandler
    {
        private bool _added;

        public (string Name, string SavePath)? Existing { get; init; }

        public string State { get; init; } = "downloading";

        public bool LoginSucceeds { get; init; } = true;

        public int Logins { get; private set; }

        public HttpStatusCode VersionStatus { get; init; } = HttpStatusCode.OK;

        /// <summary>Raw JSON members appended to the listed torrent, for the fields older clients omit.</summary>
        public string ExtraFields { get; init; } = string.Empty;

        public List<string> AddedUrls { get; } = [];

        public Dictionary<string, string> AddedFields { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/auth/login", StringComparison.Ordinal))
            {
                Logins++;
                if (!LoginSucceeds)
                {
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("Fails.") };
                }

                var login = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("Ok.") };
                login.Headers.Add("Set-Cookie", "SID=session; HttpOnly");
                return login;
            }

            if (path.EndsWith("/torrents/add", StringComparison.Ordinal))
            {
                var form = (MultipartFormDataContent)request.Content!;
                foreach (var part in form)
                {
                    var name = part.Headers.ContentDisposition?.Name?.Trim('"') ?? string.Empty;
                    if (part is StringContent)
                    {
                        AddedFields[name] = await part.ReadAsStringAsync(cancellationToken);
                    }

                    if (name == "urls")
                    {
                        AddedUrls.Add(await part.ReadAsStringAsync(cancellationToken));
                    }
                }

                _added = true;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("Ok.") };
            }

            if (path.EndsWith("/app/version", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(VersionStatus) { Content = new StringContent("v5.0.0") };
            }

            if (path.EndsWith("/torrents/info", StringComparison.Ordinal))
            {
                var listed = Existing ?? (_added ? ("Film", Staging) : null);
                var json = listed is { } torrent
                    ? $$"""[{"hash":"{{Hash}}","name":"{{torrent.Name}}","state":"{{State}}","progress":0.1,"save_path":{{System.Text.Json.JsonSerializer.Serialize(torrent.SavePath)}}{{ExtraFields}}}]"""
                    : "[]";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    /// <summary>An indexer that answers every request with a body of the given size.</summary>
    private sealed class OversizedIndexer(int bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[bytes]) });
    }

    private sealed class NeverAnswers : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class NoHttpClients : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("No .torrent should be fetched here.");
    }
}
