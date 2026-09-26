using Cinomni.Downloads.Application;
using Cinomni.Downloads.Contracts;
using Cinomni.Downloads.Engine;
using Cinomni.Playback.Application;
using Cinomni.Playback.Encoding;
using Cinomni.Subtitles.Application;
using Cinomni.Subtitles.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Host.Tests;

/// <summary>
/// Pins the composition root's configuration contract for the adapters a packaged installation must be
/// able to point somewhere else: the sidecar endpoint and staging directory, the transcode root and
/// FFmpeg, and the subtitle profile and provider key. Until this landed those five option objects were
/// stuck at their code defaults and no environment variable could move them.
/// <para>
/// The second half of each case matters as much as the first: the binders read key by key, so a typo must
/// leave the code default in place rather than silently reshape an adapter into something half-configured.
/// </para>
/// </summary>
public sealed class ModuleConfigurationTests
{
    [Fact]
    public void The_sidecar_endpoint_and_staging_path_come_from_configuration()
    {
        var options = Resolve<SidecarOptions>(services => services.AddConfiguredDownloadsAdapters(
            Configuration(new Dictionary<string, string?>
            {
                ["Downloads:Sidecar:Address"] = "http://torrent-sidecar:50051",
                ["Downloads:Sidecar:StagingPath"] = "/media/staging",
            })));

        Assert.Equal("http://torrent-sidecar:50051", options.Address);
        Assert.Equal("/media/staging", options.StagingPath);
    }

    [Fact]
    public void A_misspelled_downloads_key_leaves_the_code_default()
    {
        var options = Resolve<SidecarOptions>(services => services.AddConfiguredDownloadsAdapters(
            Configuration(new Dictionary<string, string?>
            {
                // "StagePath", not "StagingPath": the wrong key must not blank the staging directory.
                ["Downloads:Sidecar:StagePath"] = "/media/staging",
            })));

        Assert.Equal("/data/downloads", options.StagingPath);
        Assert.Equal("http://localhost:50051", options.Address);
    }

    [Fact]
    public void The_control_credential_and_the_call_deadline_come_from_configuration()
    {
        var options = Resolve<SidecarOptions>(services => services.AddConfiguredDownloadsAdapters(
            Configuration(new Dictionary<string, string?>
            {
                ["Downloads:Sidecar:ControlToken"] = "a-token-supplied-by-the-deployment",
                ["Downloads:Sidecar:CallTimeout"] = "00:00:05",
            })));

        Assert.Equal("a-token-supplied-by-the-deployment", options.ControlToken);
        Assert.Equal(TimeSpan.FromSeconds(5), options.CallTimeout);

        // Unset is the development state: an open control port on a private network, which the sidecar
        // and the startup line both report. It must not become an empty *required* credential.
        var unset = Resolve<SidecarOptions>(
            services => services.AddConfiguredDownloadsAdapters(Configuration([])));
        Assert.Equal(string.Empty, unset.ControlToken);
        Assert.Equal(TimeSpan.FromSeconds(15), unset.CallTimeout);
    }

    [Fact]
    public void No_tunnel_section_leaves_the_guard_off_entirely()
    {
        // The opt-in guarantee at the composition root: an installation that configures nothing gets
        // no device, and with no device nothing about its downloads changes.
        var options = new TunnelOptions();

        ModuleConfiguration.ApplyTunnelGuard(Configuration([]), options);

        Assert.False(options.IsConfigured);
        Assert.Equal(TunnelLossPolicy.Block, options.LossPolicy);
        options.Validate();
    }

    [Fact]
    public void Transfer_limits_come_from_configuration_and_a_bound_can_be_lifted()
    {
        var options = new TransferOptions();

        ModuleConfiguration.ApplyTransfers(
            Configuration(new Dictionary<string, string?>
            {
                ["Downloads:Transfers:StallTimeout"] = "06:00:00",
                ["Downloads:Transfers:SeedRatioLimit"] = "none",
                ["Downloads:Transfers:SeedTimeLimit"] = "2.00:00:00",
            }),
            options);

        Assert.Equal(TimeSpan.FromHours(6), options.StallTimeout);
        Assert.Null(options.SeedRatioLimit);
        Assert.Equal(TimeSpan.FromDays(2), options.SeedTimeLimit);
        options.Validate();
    }

    [Fact]
    public void Transfer_limits_left_out_keep_their_defaults()
    {
        var options = new TransferOptions();

        ModuleConfiguration.ApplyTransfers(
            Configuration(new Dictionary<string, string?> { ["Downloads:Transfers:SeedTimeLimit"] = "" }),
            options);

        Assert.Equal(TimeSpan.FromHours(24), options.StallTimeout);
        Assert.Equal(1.0, options.SeedRatioLimit);
        Assert.Null(options.SeedTimeLimit); // present and empty: no bound on time
    }

    [Fact]
    public void The_tunnel_guard_comes_from_configuration()
    {
        var options = new TunnelOptions();

        ModuleConfiguration.ApplyTunnelGuard(
            Configuration(new Dictionary<string, string?>
            {
                ["Downloads:Tunnel:Device"] = " tun0 ",
                ["Downloads:Tunnel:LossPolicy"] = "pauseandalert",
                ["Downloads:Tunnel:PollInterval"] = "00:00:10",
                ["Downloads:Tunnel:UnverifiedThreshold"] = "3",
                ["Downloads:Tunnel:VerifiedThreshold"] = "4",
            }),
            options);

        // Trimmed at the boundary, because the name has to match the interface the sidecar bound.
        Assert.Equal("tun0", options.Device);
        Assert.Equal(TunnelLossPolicy.PauseAndAlert, options.LossPolicy);
        Assert.Equal(TimeSpan.FromSeconds(10), options.PollInterval);
        Assert.Equal(3, options.UnverifiedThreshold);
        Assert.Equal(4, options.VerifiedThreshold);
        options.Validate();
    }

    [Theory]
    [InlineData("pause-and-alert")]
    [InlineData("PauseAndAlert")]
    [InlineData("pause_and_alert")]
    public void The_sidecar_spelling_of_a_policy_is_the_same_setting(string value)
    {
        // One `.env` variable configures the guard in two processes. If the backend understood only
        // its own casing, an operator who asked for PauseAndAlert would get it in the sidecar and
        // Block here — two halves of one kill-switch enforcing different rules.
        var options = new TunnelOptions();

        ModuleConfiguration.ApplyTunnelGuard(
            Configuration(new Dictionary<string, string?> { ["Downloads:Tunnel:LossPolicy"] = value }),
            options);

        Assert.Equal(TunnelLossPolicy.PauseAndAlert, options.LossPolicy);
    }

    [Theory]
    [InlineData("blok")]
    [InlineData("off")]
    [InlineData("false")]
    [InlineData("none")]
    public void A_policy_value_that_is_not_one_of_the_three_becomes_Block(string value)
    {
        // The one setting where an unreadable value must not mean "leave it as it was". Every other
        // binder falls back to the code default; a typo here would silently disable a kill-switch, so
        // a value that was written and cannot be understood becomes the strongest mode instead.
        var options = new TunnelOptions { LossPolicy = TunnelLossPolicy.Ignore };

        ModuleConfiguration.ApplyTunnelGuard(
            Configuration(new Dictionary<string, string?> { ["Downloads:Tunnel:LossPolicy"] = value }),
            options);

        Assert.Equal(TunnelLossPolicy.Block, options.LossPolicy);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_policy_is_unset_rather_than_unreadable_and_leaves_the_code_default(string value)
    {
        // Blank is how an environment variable that was never given a value arrives, and the code
        // default it falls back to is Block — so the fail-closed outcome is the same either way.
        var options = new TunnelOptions();

        ModuleConfiguration.ApplyTunnelGuard(
            Configuration(new Dictionary<string, string?> { ["Downloads:Tunnel:LossPolicy"] = value }),
            options);

        Assert.Equal(TunnelLossPolicy.Block, options.LossPolicy);
    }

    [Fact]
    public void The_transcode_root_and_ffmpeg_settings_come_from_configuration()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["Playback:TranscodeRoot"] = "/media/transcodes",
            ["Playback:Ffmpeg:BinaryPath"] = "/usr/bin/ffmpeg",
            ["Playback:Ffmpeg:SegmentSeconds"] = "4",
        });

        Assert.Equal(
            "/media/transcodes",
            Resolve<PlaybackOptions>(
                services => services.AddConfiguredPlaybackAdapters(configuration, NoRetention)).TranscodeRoot);

        var ffmpeg = Resolve<FfmpegEncoderOptions>(
            services => services.AddConfiguredPlaybackAdapters(configuration, NoRetention));
        Assert.Equal("/usr/bin/ffmpeg", ffmpeg.BinaryPath);
        Assert.Equal(4, ffmpeg.SegmentSeconds);
    }

    [Fact]
    public void The_transcode_limits_and_idle_timeout_come_from_configuration()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["Playback:MaxConcurrentTranscodes"] = "6",
            ["Playback:MaxTranscodesPerAccount"] = "3",
            ["Playback:TranscodeIdleTimeout"] = "00:10:00",
            ["Playback:MaxTranscodeLifetime"] = "04:00:00",
        });

        var options = Resolve<PlaybackOptions>(
            services => services.AddConfiguredPlaybackAdapters(configuration, NoRetention));

        Assert.Equal(6, options.MaxConcurrentTranscodes);
        Assert.Equal(3, options.MaxTranscodesPerAccount);
        Assert.Equal(TimeSpan.FromMinutes(10), options.TranscodeIdleTimeout);
        Assert.Equal(TimeSpan.FromHours(4), options.MaxTranscodeLifetime);
    }

    [Theory]
    [InlineData("Playback:MaxConcurrentTranscodes", "0")]
    [InlineData("Playback:MaxTranscodesPerAccount", "0")]
    // A paused player in a background tab reports about once a minute: shorter would cut it off.
    [InlineData("Playback:TranscodeIdleTimeout", "00:00:30")]
    // Shorter than a film would cut viewers off part way through.
    [InlineData("Playback:MaxTranscodeLifetime", "00:30:00")]
    public void An_unusable_transcode_limit_stops_startup(string key, string value)
    {
        var configuration = Configuration(new Dictionary<string, string?> { [key] = value });

        var failure = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddConfiguredPlaybackAdapters(configuration, NoRetention));

        Assert.Contains(key, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_hardware_probe_launches_the_same_ffmpeg_binary_configured_for_encoding()
    {
        // Probing a different binary than the one the encoder actually launches would answer for
        // hardware the encoder never gets to use.
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["Playback:Ffmpeg:BinaryPath"] = "/usr/bin/ffmpeg",
        });

        var probe = Resolve<HardwareCapabilityProbeOptions>(
            services => services.AddConfiguredPlaybackAdapters(configuration, NoRetention));

        Assert.Equal("/usr/bin/ffmpeg", probe.BinaryPath);
    }

    [Fact]
    public void The_render_device_path_comes_from_configuration_and_otherwise_keeps_its_default()
    {
        var configured = Resolve<HardwareCapabilityProbeOptions>(services => services.AddConfiguredPlaybackAdapters(
            Configuration(new Dictionary<string, string?>
            {
                ["Playback:HardwareAcceleration:DevicePath"] = "/dev/dri/renderD129",
            }),
            NoRetention));
        Assert.Equal("/dev/dri/renderD129", configured.DevicePath);

        var defaulted = Resolve<HardwareCapabilityProbeOptions>(services => services.AddConfiguredPlaybackAdapters(
            Configuration(new Dictionary<string, string?>()), NoRetention));
        Assert.Equal("/dev/dri/renderD128", defaulted.DevicePath);
    }

    [Fact]
    public void The_codec_whitelists_stay_in_code_and_are_not_configuration()
    {
        // They are a security boundary, not an operator setting: no configuration key may widen them.
        var ffmpeg = Resolve<FfmpegEncoderOptions>(services => services.AddConfiguredPlaybackAdapters(
            Configuration(new Dictionary<string, string?>
            {
                ["Playback:Ffmpeg:AllowedVideoCodecs:0"] = "anything",
                ["Playback:Ffmpeg:AllowedAudioCodecs:0"] = "anything",
            }),
            NoRetention));

        Assert.Equal(["copy", "libx264", "libx265"], ffmpeg.AllowedVideoCodecs.Order(StringComparer.Ordinal));
        Assert.Equal(["aac", "copy"], ffmpeg.AllowedAudioCodecs.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_subtitle_profile_comes_from_configuration()
    {
        var profile = Resolve<SubtitleOptions>(services => services.AddConfiguredSubtitleAdapters(
            Configuration(new Dictionary<string, string?>
            {
                ["Subtitles:WantedLanguages:0"] = "es",
                ["Subtitles:WantedLanguages:1"] = "fr",
                ["Subtitles:MinScore"] = "8",
                ["Subtitles:HearingImpaired"] = "true",
                ["Subtitles:Forced"] = "true",
                ["Subtitles:ProviderCallInterval"] = "00:00:05",
            })));

        Assert.Equal(["es", "fr"], profile.WantedLanguages);
        Assert.Equal(8, profile.MinScore);
        Assert.True(profile.HearingImpaired);
        Assert.True(profile.Forced);
        Assert.Equal(TimeSpan.FromSeconds(5), profile.ProviderCallInterval);
    }

    [Fact]
    public void An_absent_language_list_leaves_the_default_rather_than_disabling_subtitles()
    {
        var profile = Resolve<SubtitleOptions>(services => services.AddConfiguredSubtitleAdapters(
            Configuration(new Dictionary<string, string?> { ["Subtitles:MinScore"] = "8" })));

        Assert.Equal(["en"], profile.WantedLanguages);
    }

    [Fact]
    public void The_subtitle_provider_key_comes_from_configuration_and_a_blank_one_disables_it()
    {
        var configured = Resolve<SubtitleProviderOptions>(services => services.AddConfiguredSubtitleAdapters(
            Configuration(new Dictionary<string, string?>
            {
                ["Subtitles:Provider:ApiKey"] = "provider-key-from-the-environment",
                ["Subtitles:Provider:UserAgent"] = "Cinomni/test",
                ["Subtitles:Provider:Timeout"] = "00:00:10",
            })));

        Assert.Equal("provider-key-from-the-environment", configured.ApiKey);
        Assert.Equal("Cinomni/test", configured.UserAgent);
        Assert.Equal(TimeSpan.FromSeconds(10), configured.Timeout);

        // Unset is the shipped state: the adapter stays registered but sends no key, which is how the
        // module degrades instead of failing an installation that never configured a provider.
        var unset = Resolve<SubtitleProviderOptions>(
            services => services.AddConfiguredSubtitleAdapters(Configuration([])));
        Assert.Equal(string.Empty, unset.ApiKey);
        Assert.Equal("https://api.opensubtitles.com/api/v1/", unset.BaseAddress);
    }

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    /// <summary>
    /// Playback carries its retention window on the same options object the configuration binder fills,
    /// so the binder takes the retention callback as well. These cases assert the configuration half; the
    /// retention half has its own coverage in <c>RetentionWiringTests</c>, so they leave it at its default.
    /// </summary>
    private static void NoRetention(PlaybackOptions options)
    {
    }

    /// <summary>Registers the adapters into a fresh container and resolves one of the option objects.</summary>
    private static T Resolve<T>(Action<IServiceCollection> register)
        where T : notnull
    {
        var services = new ServiceCollection();
        register(services);

        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<T>();
    }
}
