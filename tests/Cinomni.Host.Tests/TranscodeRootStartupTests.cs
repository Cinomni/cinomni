using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cinomni.Host.Tests;

/// <summary>
/// Playback deletes inside its transcode root on its own initiative, so a root that shares a tree with
/// the library or the download staging area must stop startup — driven through the real composition
/// root, so the roots come from the options the modules were actually configured with.
/// </summary>
public sealed class TranscodeRootStartupTests
{
    [Theory]
    [InlineData("Import:LibraryRoot", "/data/library", "/data/library/transcodes")]
    [InlineData("Import:LibraryRoot", "/data/library", "/data")]
    [InlineData("Downloads:Sidecar:StagingPath", "/data/downloads", "/data/downloads")]
    public void A_transcode_root_that_shares_a_tree_with_media_stops_startup(string key, string root, string transcodeRoot)
    {
        var provider = Compose(new Dictionary<string, string?>
        {
            [key] = root,
            ["Playback:TranscodeRoot"] = transcodeRoot,
        });

        var failure = Assert.Throws<InvalidOperationException>(() => TranscodeRootStartup.VerifyRootIsSeparate(provider));

        Assert.Contains("Playback:TranscodeRoot", failure.Message, StringComparison.Ordinal);
        Assert.Contains(key, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_packaged_layout_keeps_the_transcode_root_apart()
    {
        var provider = Compose(new Dictionary<string, string?>
        {
            ["Import:LibraryRoot"] = "/data/library",
            ["Downloads:Sidecar:StagingPath"] = "/data/downloads",
            ["Playback:TranscodeRoot"] = "/data/transcodes",
        });

        TranscodeRootStartup.VerifyRootIsSeparate(provider);
    }

    private static ServiceProvider Compose(Dictionary<string, string?> values)
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.None));
        services.AddCinomniModules(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build(), HostComposition.UnusableConnectionString);
        return services.BuildServiceProvider();
    }
}
