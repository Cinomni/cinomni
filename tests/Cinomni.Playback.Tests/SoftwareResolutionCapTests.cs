using System.Collections.Immutable;
using Cinomni.Library.Contracts;
using Cinomni.Operations.Settings;
using Cinomni.Playback.Contracts;
using Cinomni.Playback.Encoding;
using Cinomni.Playback.Planning;
using Microsoft.Extensions.Configuration;

namespace Cinomni.Playback.Tests;

/// <summary>
/// A CPU rarely converts 4K as fast as it plays, so a software transcode is held to a smaller picture by
/// default — said in the plan, liftable by the operator, and never applied to a hardware encoder.
/// </summary>
public sealed class SoftwareResolutionCapTests
{
    private readonly HardwareCapabilitiesCache _hardwareCapabilities = new();
    private readonly MutableLiveOptions<HardwareTranscodingOptions> _hardwareTranscoding = new(new HardwareTranscodingOptions());
    private readonly MutableLiveOptions<TranscodingOptions> _transcoding = new(TranscodingOptions.Default);
    private readonly PlaybackPlanner _planner;

    public SoftwareResolutionCapTests() =>
        _planner = new PlaybackPlanner(new EncoderBackendSelector(), _hardwareCapabilities, _hardwareTranscoding, _transcoding);

    /// <summary>A 4K HDR HEVC film, and a 4K screen whose browser does not play HEVC.</summary>
    private static readonly PlaybackSource UhdHdr = new(
        "matroska", "hevc", 2160, ["aac"], VideoWidth: 3840, VideoRange: VideoRangeType.Hdr10);

    private static readonly ClientCapability UhdScreen = new(["matroska", "mp4"], ["h264"], ["aac"], MaxHeight: 2160);

    [Fact]
    public void A_software_transcode_of_4k_is_brought_down_to_1080_and_the_plan_says_why()
    {
        var plan = _planner.Plan(UhdHdr, UhdScreen);

        Assert.Equal(PlaybackMethod.Transcode, plan.Method);
        Assert.Equal(EncoderBackend.Software, plan.Backend);
        Assert.Equal(1080, plan.TargetMaxHeight);
        Assert.Contains(plan.Decisions, d => d is { Property: "softwareMaxHeight", Expected: "1080", Actual: "2160", Verdict: "fail" });
        Assert.Contains(plan.TranscodeReasons, r => r.Contains("Software encoder maximum resolution", StringComparison.Ordinal));
    }

    [Fact]
    public void A_source_that_already_fits_is_recorded_as_fitting_and_keeps_its_size()
    {
        var plan = _planner.Plan(UhdHdr with { VideoHeight = 1080, VideoWidth = 1920 }, UhdScreen);

        Assert.Null(plan.TargetMaxHeight);
        Assert.Contains(plan.Decisions, d => d is { Property: "softwareMaxHeight", Verdict: "ok" });
    }

    [Fact]
    public void An_operator_who_lifts_the_ceiling_gets_the_full_size_back()
    {
        _transcoding.Current = TranscodingOptions.Default with { SoftwareMaxHeight = null };

        var plan = _planner.Plan(UhdHdr, UhdScreen);

        Assert.Null(plan.TargetMaxHeight);
        Assert.DoesNotContain(plan.Decisions, d => d.Property == "softwareMaxHeight");
    }

    [Fact]
    public void A_hardware_encoder_is_not_held_to_the_software_ceiling()
    {
        _hardwareCapabilities.Publish(new HardwareCapabilities([EncoderBackend.Vaapi]));

        var plan = _planner.Plan(UhdHdr, UhdScreen);

        Assert.Equal(EncoderBackend.Vaapi, plan.Backend);
        Assert.Null(plan.TargetMaxHeight);
        Assert.DoesNotContain(plan.Decisions, d => d.Property == "softwareMaxHeight");
    }

    [Fact]
    public void The_smallest_ceiling_wins_when_a_viewer_or_the_server_asks_for_less()
    {
        _transcoding.Current = TranscodingOptions.Default with { MaxHeight = 720 };

        var plan = _planner.Plan(UhdHdr, UhdScreen);

        Assert.Equal(720, plan.TargetMaxHeight);
    }

    [Theory]
    [InlineData(null, 1080)]
    [InlineData("original", null)]
    [InlineData("720", 720)]
    public void The_setting_binds_to_a_ceiling_or_to_none(string? value, int? expected)
    {
        var values = value is null
            ? ImmutableDictionary<string, string>.Empty
            : ImmutableDictionary<string, string>.Empty.Add(TranscodingSettingDefinitions.SoftwareMaxResolution.Key, value);
        var view = new SettingsView(new SettingsSnapshot(1, values), new ConfigurationBuilder().Build());

        var options = TranscodingSettingDefinitions.Bind(view);

        Assert.Equal(expected, options.SoftwareMaxHeight);
    }

    /// <summary>A settable <see cref="ILiveOptions{TOptions}"/> stand-in for a pure test.</summary>
    private sealed class MutableLiveOptions<TOptions>(TOptions initial) : ILiveOptions<TOptions>
        where TOptions : class
    {
        public TOptions Current { get; set; } = initial;
    }
}
