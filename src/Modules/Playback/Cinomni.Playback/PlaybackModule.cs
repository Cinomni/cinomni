using Cinomni.Kernel.Messaging;
using Cinomni.Operations;
using Cinomni.Operations.Settings;
using Cinomni.Playback.Application;
using Cinomni.Playback.Contracts;
using Cinomni.Playback.Encoding;
using Cinomni.Playback.Messaging;
using Cinomni.Playback.Persistence;
using Cinomni.Playback.Planning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Cinomni.Playback;

/// <summary>
/// Registers the Playback module: plans how to deliver an asset (DirectPlay/Remux/Transcode,
/// explainable), runs the session, tracks per-user resume progress, and serves the media
/// (direct file or HLS). Reads assets from Library by interface; never mutates them. Requires the
/// platform kernel. The FFmpeg encoder is registered separately (<see cref="AddPlaybackAdapters"/>) so
/// tests can substitute a fake.
/// </summary>
public static class PlaybackModule
{
    public static IServiceCollection AddPlaybackModule(this IServiceCollection services)
    {
        services.AddModuleDbContext<PlaybackDbContext>();

        services.AddIntegrationEvent<PlaybackStarted>(PlaybackEventNames.PlaybackStarted);
        services.AddIntegrationEvent<PlaybackProgressUpdated>(PlaybackEventNames.PlaybackProgressUpdated);
        services.AddIntegrationEvent<PlaybackCompleted>(PlaybackEventNames.PlaybackCompleted);
        services.AddIntegrationEvent<TranscodeStarted>(PlaybackEventNames.TranscodeStarted);
        services.AddIntegrationEvent<TranscodeFailed>(PlaybackEventNames.TranscodeFailed);

        services.AddCommand<PurgePlaybackSessionsCommand>(PlaybackCommandNames.PurgeSessions);

        // Pure, no external config: safe to register unconditionally so a test host that skips
        // AddPlaybackAdapters (and therefore never runs the real probe) still resolves the planner,
        // starting with an empty HardwareCapabilities and so always planning software transcodes.
        services.AddSingleton<HardwareCapabilitiesCache>();
        services.AddSingleton<IEncoderBackendSelector, EncoderBackendSelector>();

        // The operator's hardware-transcoding on/off switch (Console → Settings). Its own tiny options
        // type, not a property on PlaybackOptions — see HardwareTranscodingOptions for why — so it can
        // be registered here rather than in AddPlaybackAdapters, alongside the planner that reads it.
        HardwareTranscodingOptions BindHardwareTranscoding(SettingsView view) => new()
        {
            Enabled = view.GetBool(HardwareTranscodingSettingDefinitions.Enabled) ?? true,
        };

        services.AddSettingDefinition(HardwareTranscodingSettingDefinitions.Enabled);
        services.AddLiveOptions(BindHardwareTranscoding);
        foreach (var definition in TranscodingSettingDefinitions.All)
        {
            services.AddSettingDefinition(definition);
        }

        services.AddLiveOptions(TranscodingSettingDefinitions.Bind);

        services.AddSingleton<IPlaybackPlanner, PlaybackPlanner>();
        // The FFmpeg processes this node is running, with their slots and idle clocks: in memory, one
        // per process, because that is where a child process lives too.
        services.AddSingleton<ActiveTranscodes>();
        // Stops what a previous run left running — and only that: the recorded process, recognised by its
        // id, its start, its binary and its own command line. It needs to know which binary FFmpeg is; a
        // host without the adapters (a test host) gets the default, and the adapters replace it.
        services.TryAddSingleton(new FfmpegEncoderOptions());
        // A host without the adapters takes every file for SDR; the adapters replace this with ffprobe.
        services.TryAddSingleton<IVideoRangeProbe, UnknownVideoRangeProbe>();
        services.AddSingleton<OrphanedTranscodeTerminator>();
        services.AddScoped<SessionUpdates>();
        services.AddScoped<SessionCloser>();
        services.AddScoped<TranscodeSweep>();
        services.AddScoped<IPlaybackSessionCommands, PlaybackService>();
        services.AddScoped<IPlaybackQuery, PlaybackQuery>();
        services.AddScoped<PlaybackStreamer>();
        // Subtitles as WebVTT, converted on first ask and kept (bounded) for the switches that follow.
        services.AddSingleton<SubtitleCache>();
        services.AddScoped<PlaybackSubtitles>();
        // Owns the transcode directories the purge has to reclaim before it deletes the rows naming
        // them. Stateless over the options, so one instance serves every sweep.
        services.AddSingleton<TranscodeWorkspace>();
        services.AddScoped<ICommandHandler<PurgePlaybackSessionsCommand>, PurgePlaybackSessionsCommandHandler>();

        return services;
    }

    /// <summary>
    /// Registers the production FFmpeg encoder, the hardware capability probe, and the playback
    /// options. Tests skip this and register their own <see cref="IMediaEncoder"/> fake plus a
    /// <see cref="PlaybackOptions"/>. <see cref="HardwareCapabilityProbeOptions"/> is shared by both
    /// adapters: the probe uses its device path to detect VAAPI/QSV, and <see cref="FfmpegMediaEncoder"/>
    /// reuses the same path to attach the device for an actual VAAPI encode — one render-node setting,
    /// not two.
    /// </summary>
    public static IServiceCollection AddPlaybackAdapters(
        this IServiceCollection services,
        Action<PlaybackOptions>? configurePlayback = null,
        Action<FfmpegEncoderOptions>? configureFfmpeg = null,
        Action<HardwareCapabilityProbeOptions>? configureHardwareProbe = null)
    {
        var playbackOptions = new PlaybackOptions();
        configurePlayback?.Invoke(playbackOptions);
        playbackOptions.Validate();
        services.AddSingleton(playbackOptions);

        // The retention cadence is a deployment concern read once at registration, like the metadata
        // sweeps: a test host registers the module without acquiring a background cadence.
        services.AddScheduledJob<PurgePlaybackSessionsCommand>(
            "playback.retention", PlaybackCommandNames.PurgeSessions, playbackOptions.SessionPurgeInterval);

        var ffmpegOptions = new FfmpegEncoderOptions();
        configureFfmpeg?.Invoke(ffmpegOptions);
        services.AddSingleton(ffmpegOptions);

        services.AddSingleton<IMediaEncoder, FfmpegMediaEncoder>();
        services.AddSingleton<ISubtitleConverter, FfmpegSubtitleConverter>();
        services.AddSingleton<IVideoRangeProbe, FfprobeVideoRangeProbe>();

        // Records how each FFmpeg ended, stops the ones nobody is watching, recovers what a previous
        // run left on disk, and stops everything on shutdown. A test host drives TranscodeSweep itself.
        services.AddHostedService<TranscodeReaper>();

        var hardwareProbeOptions = new HardwareCapabilityProbeOptions();
        configureHardwareProbe?.Invoke(hardwareProbeOptions);
        services.AddSingleton(hardwareProbeOptions);
        services.AddSingleton<IHardwareCapabilityProbe, FfmpegHardwareCapabilityProbe>();

        return services;
    }

    /// <summary>Applies pending migrations for the playback schema (idempotent).</summary>
    public static async Task MigratePlaybackAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PlaybackDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);
    }
}
