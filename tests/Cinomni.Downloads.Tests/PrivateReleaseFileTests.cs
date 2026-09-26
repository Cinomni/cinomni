using Cinomni.Discovery.Contracts;
using Cinomni.Downloads.Application;
using Cinomni.Kernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Downloads.Tests;

/// <summary>
/// A private tracker's release link can only be fetched as the signed-in member, and only Discovery
/// holds that session. These pin the hand-off: the engine gets the file Discovery fetched, a link no
/// such indexer owns is left to the engine exactly as before, and a checkpoint needs nothing fetched.
/// </summary>
public sealed class PrivateReleaseFileTests : IAsyncLifetime
{
    private static readonly byte[] TorrentBytes = "d4:infod4:name1:aee"u8.ToArray();

    private readonly FakeTorrentEngine _engine = new();
    private readonly ScriptedReleaseFiles _files = new();
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync() =>
        _provider = await DownloadsTestHost.CreateAsync("cinomni_test_downloads_private_files", _engine, services =>
            services.AddSingleton<IReleaseFileSource>(_files));

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task A_member_only_link_reaches_the_engine_as_the_file_discovery_fetched()
    {
        _files.Answer = new ReleaseFile(ReleaseFileOutcome.TorrentFile, TorrentBytes);

        await AddAsync("https://private.example/download.php?id=1");

        var added = Assert.Single(_engine.Adds);
        Assert.Equal(TorrentBytes, added.TorrentFile);
        // The link is still the task's own record of where the release came from.
        Assert.Equal("https://private.example/download.php?id=1", added.DownloadUrl);
    }

    [Fact]
    public async Task A_link_answered_with_a_magnet_reaches_the_engine_as_that_magnet()
    {
        _files.Answer = new ReleaseFile(ReleaseFileOutcome.Magnet, Magnet: "magnet:?xt=urn:btih:abc");

        await AddAsync("https://private.example/download.php?id=2");

        var added = Assert.Single(_engine.Adds);
        Assert.Equal("magnet:?xt=urn:btih:abc", added.DownloadUrl);
        Assert.Null(added.TorrentFile);
    }

    [Fact]
    public async Task A_link_no_signed_in_indexer_owns_is_left_to_the_engine_and_a_magnet_is_never_asked_about()
    {
        _files.Answer = ReleaseFile.NotHandled;

        await AddAsync("https://public.example/file.torrent");
        await AddAsync("magnet:?xt=urn:btih:def");

        Assert.All(_engine.Adds, added => Assert.Null(added.TorrentFile));
        Assert.Equal(["https://public.example/file.torrent"], _files.Asked);
    }

    [Fact]
    public async Task A_release_file_that_cannot_be_fetched_fails_the_add_so_its_command_retries()
    {
        _files.Failure = new HttpRequestException("The tracker did not answer with a release file.");

        await Assert.ThrowsAsync<HttpRequestException>(() => AddAsync("https://private.example/download.php?id=3"));
        Assert.Empty(_engine.Adds);
    }

    private async Task AddAsync(string downloadUrl)
    {
        await using var scope = _provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DownloadService>().AddDownloadAsync(
            Uuid7.New(), Uuid7.New(), Uuid7.New(), Uuid7.New(), "release", downloadUrl);
    }

    private sealed class ScriptedReleaseFiles : IReleaseFileSource
    {
        public ReleaseFile Answer { get; set; } = ReleaseFile.NotHandled;

        public Exception? Failure { get; set; }

        public List<string> Asked { get; } = [];

        public Task<ReleaseFile> FetchAsync(string downloadUrl, CancellationToken cancellationToken = default)
        {
            Asked.Add(downloadUrl);
            return Failure is null ? Task.FromResult(Answer) : Task.FromException<ReleaseFile>(Failure);
        }
    }
}
