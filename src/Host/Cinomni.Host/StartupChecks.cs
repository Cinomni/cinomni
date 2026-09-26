using Cinomni.Downloads.Contracts;
using Cinomni.Downloads.Engine;
using Cinomni.Import.Application;
using Cinomni.Import.Files;
using Cinomni.Import.Probe;
using Cinomni.Playback.Application;
using Cinomni.Playback.Encoding;

namespace Cinomni.Host;

/// <summary>
/// The startup half of the packaging contract. A containerised installation gets its storage from bind
/// mounts and its tools from the image, and both can be wrong in ways the modules only discover much
/// later: Import defers forever when the library root is missing, and a download lands in a staging
/// directory nothing can read back. These checks turn that into a failure at boot, where the log still
/// says what happened.
/// <para>
/// The split is deliberate, and it is drawn where recovery stops being possible. The library root and
/// the download staging path are the import contract — a completed download is hardlinked from one into
/// the other — so a process that cannot write them has nothing to fall back on and must stop. Everything
/// else degrades a single feature the adapters already report on: the transcode root is scratch (a
/// transcode fails explainably per session and direct play is unaffected), a missing external tool
/// disables the feature that launches it, and an unset provider key leaves that provider unavailable.
/// Those are warnings, because turning them into a crash under <c>restart: unless-stopped</c> would
/// trade one broken feature for an installation with no interface at all.
/// </para>
/// <para>
/// What a writable-root check proves is writability by this account, and nothing more: it creates the
/// directory when it is absent, so it cannot tell a mounted volume from an empty mount point. The
/// failure it does catch is the common one — a root that exists and belongs to somebody else. What it
/// cannot catch is the pair of roots that are each writable and cannot be <em>linked</em>, which is why
/// a hardlink between them is probed as well: that one degrades silently instead of failing, so nothing
/// else in the installation would ever mention it.
/// </para>
/// <para>
/// Only configuration <em>key names</em> are ever logged. A credential value never reaches a log record
/// (SECURITY.md, secrets and credentials).
/// </para>
/// </summary>
internal static class StartupChecks
{
    /// <summary>
    /// Credentials whose absence disables a provider instead of stopping the installation. The one
    /// mandatory secret — the database connection string — already fails fast in <c>Program</c>.
    /// The metadata provider keys are not here: an administrator can also enter them under Settings,
    /// so configuration alone cannot say they are missing. Each provider says so itself, once, at its
    /// first use (<c>DisabledProviderNotice</c>).
    /// </summary>
    private static readonly string[] OptionalCredentialKeys =
    [
        "Subtitles:Provider:ApiKey",
    ];

    /// <summary>
    /// Verifies the production runtime contract before any migration runs. Outside Production this is a
    /// no-op: a development checkout must not have directories created for it because a container default
    /// pointed at an absolute path that means something else on a developer's machine.
    /// <para>
    /// A violation is reported rather than thrown, because the reader of this failure is an operator with
    /// a mis-mounted volume and not a developer with a debugger. They get the sentence, once, through the
    /// logger the installation is already configured with, and the process leaves with a code that says
    /// the configuration is wrong — instead of an unhandled exception that buries the useful line in a
    /// stack trace and ends the process on a signal.
    /// </para>
    /// </summary>
    /// <returns>
    /// <c>null</c> when the contract holds, and when the environment is not Production. Otherwise the exit
    /// code the process must take, having already said why.
    /// </returns>
    public static int? VerifyRuntimeContract(WebApplication app)
    {
        if (!app.Environment.IsProduction())
        {
            return null;
        }

        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(StartupChecks));

        try
        {
            VerifyRuntimeContract(app.Services, app.Configuration, logger);
            return null;
        }
        catch (StartupContractException violation)
        {
            // Exactly this type, and nothing wider: a failure to compose or to bind configuration is a
            // defect and must keep crashing with its stack trace. The cause is folded into the sentence
            // rather than attached as an exception, so the answer is the first line and not the last.
            var cause = violation.InnerException is { } inner
                ? $" The platform reported: {inner.Message}"
                : string.Empty;

            logger.LogCritical(
                "Cinomni cannot start. {Violation}{Cause} Nothing has been migrated and nothing is "
                + "serving; correct the configuration and start it again.",
                violation.Message,
                cause);

            return StartupContractException.ExitCode;
        }
    }

    /// <summary>
    /// The check itself, over an already-composed container. Separated from the <see cref="WebApplication"/>
    /// entry point so it can be driven directly by a test.
    /// </summary>
    public static void VerifyRuntimeContract(
        IServiceProvider services,
        IConfiguration configuration,
        ILogger logger)
    {
        // Fatal: the two halves of the import contract. A hardlink from staging into the library is the
        // whole acquisition path, and neither side has a degraded mode.
        var libraryRoot = services.GetRequiredService<ImportOptions>().LibraryRoot;
        var stagingPath = services.GetRequiredService<SidecarOptions>().StagingPath;
        VerifyWritableRoot("Import:LibraryRoot", libraryRoot);
        VerifyWritableRoot("Downloads:Sidecar:StagingPath", stagingPath);

        // A warning: both roots are writable, so imports run — they just stop sharing bytes with the
        // download and start costing a second full copy of everything. An operator who genuinely wants
        // copies must not be locked out of their installation over it.
        WarnWhenHardlinkNotPossible(
            logger,
            "Downloads:Sidecar:StagingPath",
            stagingPath,
            "Import:LibraryRoot",
            libraryRoot);

        // A warning: transcode output is scratch. Losing it costs just-in-time transcoding, which already
        // fails one session explainably, while direct play, the library and the whole interface keep working.
        WarnWhenRootNotWritable(
            logger,
            "Playback:TranscodeRoot",
            services.GetRequiredService<PlaybackOptions>().TranscodeRoot);

        WarnWhenExecutableMissing(
            logger,
            "Import:Ffprobe:BinaryPath",
            services.GetRequiredService<FfprobeOptions>().BinaryPath);
        WarnWhenExecutableMissing(
            logger,
            "Playback:Ffmpeg:BinaryPath",
            services.GetRequiredService<FfmpegEncoderOptions>().BinaryPath);

        WarnWhenCredentialUnset(configuration, logger);
        if (services.GetService<QbittorrentOptions>() is not null)
        {
            // There is no sidecar and no control credential to report: the external client's own
            // settings decide how its torrent traffic leaves, and what its trackers and peers may reach.
            logger.LogInformation(
                "Torrent engine: an external qBittorrent client. Its own settings govern its network use; "
                + "Cinomni's egress guard cannot observe it and fails closed if a tunnel is configured.");
            return;
        }

        ReportTorrentEgress(
            logger,
            services.GetRequiredService<TunnelOptions>(),
            services.GetRequiredService<SidecarOptions>());
    }

    /// <summary>
    /// States, at every start, how torrent traffic leaves this installation.
    /// <para>
    /// It is a log line rather than a check because there is nothing here to fail: running without a
    /// tunnel is supported and is what the packaged default does. What must never happen is an
    /// installation that <em>opted in</em> to a tunnel and then runs weaker than the default without
    /// saying so, so anything below <see cref="TunnelLossPolicy.Block"/> is reported as a warning
    /// naming the mode. The same line records whether the control port carries a credential — by
    /// presence, never by value.
    /// </para>
    /// </summary>
    public static void ReportTorrentEgress(ILogger logger, TunnelOptions tunnel, SidecarOptions sidecar)
    {
        var credential = sidecar.ControlToken.Length > 0 ? "set" : "not set";

        if (!tunnel.IsConfigured)
        {
            logger.LogInformation(
                "Torrent egress: no tunnel is configured ('Downloads:Tunnel:Device' is unset), so torrent "
                + "traffic leaves over this machine's own connection. Sidecar control credential: {Credential}.",
                credential);
            return;
        }

        if (tunnel.LossPolicy is TunnelLossPolicy.Block)
        {
            logger.LogInformation(
                "Torrent egress: bound to tunnel device '{TunnelDevice}' under the Block policy — downloads "
                + "stop whenever that path cannot be verified. Sidecar control credential: {Credential}.",
                tunnel.Device,
                credential);
            return;
        }

        logger.LogWarning(
            "Torrent egress: bound to tunnel device '{TunnelDevice}' under the {LossPolicy} policy, which is "
            + "weaker than the Block default. {Consequence} Set 'Downloads:Tunnel:LossPolicy' to Block to fail "
            + "closed. Sidecar control credential: {Credential}.",
            tunnel.Device,
            tunnel.LossPolicy,
            tunnel.LossPolicy is TunnelLossPolicy.Ignore
                ? "Downloads keep running when the tunnel cannot be verified, over whatever route exists."
                : "A single unverified observation is absorbed rather than acted on, and an operator may "
                  + "resume a held download knowingly.",
            credential);
    }

    /// <summary>
    /// Runs the one-time hardware probe, publishes its result to <see cref="HardwareCapabilitiesCache"/>
    /// so the playback planner picks it up on every plan from here on, and states at every start which
    /// backends this installation can actually use — on the same reasoning as
    /// <see cref="ReportTorrentEgress"/>: nothing here can fail the start, so a probe error or a
    /// "nothing detected" result is reported and startup continues either way. Runs in every
    /// environment, not only Production, on the same precedent as
    /// <c>BackupStartup.ReportBackupContract</c>'s tool check — an installation whose hardware isn't
    /// visible yet still transcodes in software, and a developer should see that as plainly as an
    /// operator does.
    /// </summary>
    public static async Task ReportHardwareCapabilitiesAsync(IServiceProvider services, ILogger logger)
    {
        var probe = services.GetRequiredService<IHardwareCapabilityProbe>();
        var capabilities = await probe.ProbeAsync();

        services.GetRequiredService<HardwareCapabilitiesCache>().Publish(capabilities);

        if (capabilities.AvailableBackends.Count == 0)
        {
            logger.LogInformation(
                "Hardware transcoding: no VAAPI/NVENC/QSV/AMF backend passed its test at startup. Transcoding uses "
                + "software x264.");
            return;
        }

        logger.LogInformation(
            "Hardware transcoding: detected {Backends} at startup.",
            string.Join(", ", capabilities.AvailableBackends));
    }

    /// <summary>
    /// Creates the storage root and proves this process may write into it, by putting an empty probe file
    /// there and removing it again. Existence alone is not enough: the usual container failure is a
    /// directory that exists but belongs to another user. Because the root is created when it is absent,
    /// this proves writability only — it cannot distinguish a mounted volume from an empty mount point.
    /// </summary>
    /// <exception cref="StartupContractException">The root is unset, or cannot be created or written.</exception>
    public static void VerifyWritableRoot(string settingKey, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new StartupContractException(
                $"Configuration '{settingKey}' must name a storage directory; it is not set.");
        }

        try
        {
            Directory.CreateDirectory(path);

            var probe = Path.Combine(path, $".cinomni-write-probe-{Guid.NewGuid():N}");
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            throw new StartupContractException(
                $"Storage root '{path}' (configuration '{settingKey}') is not writable by this process. " +
                "Check that the volume is mounted and owned by the account the application runs as.",
                exception);
        }
    }

    /// <summary>
    /// Proves the half of the storage contract writability cannot: that a file in the download staging
    /// root can be <em>hardlinked</em> into the library root. Two writable roots on two filesystems, or
    /// owned by two accounts, pass every other check here and then silently turn every import into a full
    /// byte-for-byte copy for the life of the installation — the one failure mode in this file that
    /// degrades instead of crashing.
    /// <para>
    /// A warning and never fatal: the import still lands, so an installation whose operator accepts
    /// copies keeps working, and the line is here for the one who does not.
    /// </para>
    /// <para>
    /// It attempts a real <c>link()</c> and never compares device numbers: separate Docker volumes on one
    /// underlying filesystem report the same device number while refusing a link across the mount points,
    /// so a <c>st_dev</c> comparison would clear exactly the deployment this catches.
    /// </para>
    /// </summary>
    public static void WarnWhenHardlinkNotPossible(
        ILogger logger,
        string sourceKey,
        string sourceRoot,
        string targetKey,
        string targetRoot) =>
        ReportHardlinkProbe(logger, sourceKey, targetKey, Hardlinks.Probe(sourceRoot, targetRoot));

    /// <summary>
    /// The reporting half, taking an outcome instead of producing one. Separated from the probe because
    /// the two failures an operator has to fix differently — a second filesystem (<c>EXDEV</c>) and a uid
    /// the kernel will not let link (<c>EPERM</c> under <c>fs.protected_hardlinks</c>) — need a machine
    /// with two filesystems, or two accounts, to happen for real. Passing the outcome in lets each of
    /// those classes be driven and asserted wherever this is built.
    /// </summary>
    public static void ReportHardlinkProbe(
        ILogger logger,
        string sourceKey,
        string targetKey,
        HardlinkProbeResult probe)
    {
        if (probe.CanHardlink)
        {
            return;
        }

        logger.LogWarning(
            "A hardlink from the download staging root (configuration '{SourceKey}') into the library root " +
            "(configuration '{TargetKey}') is not possible on this installation: {Reason}. Every import " +
            "will copy instead of hardlinking, so the library will hold a second full copy of every file " +
            "the download keeps seeding. The two roots have to be one filesystem, owned by the account " +
            "this process runs as.",
            sourceKey,
            targetKey,
            probe.Explanation);
    }

    /// <summary>
    /// The same probe, reported instead of thrown. For a scratch root the honest outcome is a named warning
    /// and a running installation: the feature that needs the directory fails on its own terms, and the
    /// operator can still reach the interface and the logs to fix it.
    /// </summary>
    public static void WarnWhenRootNotWritable(ILogger logger, string settingKey, string path)
    {
        try
        {
            VerifyWritableRoot(settingKey, path);
        }
        catch (StartupContractException exception)
        {
            logger.LogWarning(
                exception,
                "Storage root '{StorageRoot}' (configuration '{ConfigurationKey}') is not writable by this " +
                "process. The features that need it will report a failure instead of running.",
                path,
                settingKey);
        }
    }

    /// <summary>
    /// Warns when an external tool cannot be resolved. Both adapters already degrade — ffprobe registers an
    /// asset without stream details, and a transcode fails explainably — so a missing binary must not stop
    /// an installation that only ever direct-plays.
    /// </summary>
    public static void WarnWhenExecutableMissing(ILogger logger, string settingKey, string binaryPath)
    {
        if (CanResolveExecutable(binaryPath))
        {
            return;
        }

        logger.LogWarning(
            "External tool '{BinaryPath}' (configuration '{ConfigurationKey}') was not found. The features " +
            "that need it will report a failure instead of running.",
            binaryPath,
            settingKey);
    }

    /// <summary>Warns for each optional credential that is not set, naming the key and never the value.</summary>
    public static void WarnWhenCredentialUnset(IConfiguration configuration, ILogger logger)
    {
        foreach (var key in OptionalCredentialKeys)
        {
            if (!string.IsNullOrWhiteSpace(configuration[key]))
            {
                continue;
            }

            logger.LogWarning(
                "Optional credential '{ConfigurationKey}' is not set; the provider that needs it stays unavailable.",
                key);
        }
    }

    /// <summary>
    /// Whether the configured binary resolves against the process' own <c>PATH</c>: as a path when it
    /// contains a separator, otherwise by walking the search path exactly as the launcher will.
    /// </summary>
    public static bool CanResolveExecutable(string binaryPath) =>
        CanResolveExecutable(binaryPath, Environment.GetEnvironmentVariable("PATH") ?? string.Empty);

    /// <summary>The same resolution against an explicit search path, so a test need not mutate the environment.</summary>
    public static bool CanResolveExecutable(string binaryPath, string searchPath)
    {
        if (string.IsNullOrWhiteSpace(binaryPath))
        {
            return false;
        }

        if (binaryPath.Contains(Path.DirectorySeparatorChar) || binaryPath.Contains(Path.AltDirectorySeparatorChar))
        {
            return ExecutableExists(binaryPath);
        }

        var directories = searchPath.Split(
            Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return directories.Any(directory => ExecutableExists(Path.Combine(directory, binaryPath)));
    }

    private static bool ExecutableExists(string candidate)
    {
        if (File.Exists(candidate))
        {
            return true;
        }

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        // On Windows the name on PATH carries no extension; PATHEXT is what completes it.
        var extensions = Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD";
        return extensions
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(extension => File.Exists(candidate + extension));
    }
}
