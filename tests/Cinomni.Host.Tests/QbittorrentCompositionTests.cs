using Cinomni.Downloads.Application;
using Cinomni.Downloads.Engine;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cinomni.Host.Tests;

/// <summary>
/// The host composed with the qBittorrent engine, through the real composition root. It used to fail
/// before serving anything: the staging directory was registered only with the sidecar, and the backup
/// check, the startup checks and the download service all ask for it whichever engine downloads.
/// </summary>
public sealed class QbittorrentCompositionTests
{
    [Fact]
    public void The_host_composes_with_the_qbittorrent_engine_and_keeps_its_staging_directory()
    {
        using var provider = Compose(new Dictionary<string, string?>
        {
            ["Downloads:Engine"] = "Qbittorrent",
            ["Downloads:Qbittorrent:BaseAddress"] = "http://qbittorrent:8080",
            ["Downloads:Sidecar:StagingPath"] = "/media/staging",
        });

        Assert.Equal("/media/staging", provider.GetRequiredService<SidecarOptions>().StagingPath);

        using var scope = provider.CreateScope();
        Assert.IsType<QbittorrentWebEngine>(scope.ServiceProvider.GetRequiredService<ITorrentEngine>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<DownloadService>());

        BackupStartup.VerifyRootIsSeparate(provider);
        TranscodeRootStartup.VerifyRootIsSeparate(provider);

        // Readiness asks the engine in use: with no sidecar probe registered it used to report healthy
        // without asking anything.
        Assert.IsType<QbittorrentWebEngine>(provider.GetRequiredService<IDownloadEngineProbe>());
        Assert.Null(provider.GetService<SidecarHealthProbe>());
    }

    [Fact]
    public void Without_a_staging_setting_the_qbittorrent_engine_keeps_the_packaged_default()
    {
        using var provider = Compose(new Dictionary<string, string?>
        {
            ["Downloads:Engine"] = "Qbittorrent",
            ["Downloads:Qbittorrent:BaseAddress"] = "http://qbittorrent:8080",
        });

        Assert.Equal(new SidecarOptions().StagingPath, provider.GetRequiredService<SidecarOptions>().StagingPath);
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
