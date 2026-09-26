using Cinomni.Downloads.Contracts;
using Cinomni.Downloads.Engine;
using Cinomni.Import.Application;
using Cinomni.Import.Files;
using Cinomni.Import.Probe;
using Cinomni.Playback.Application;
using Cinomni.Playback.Contracts;
using Cinomni.Playback.Encoding;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cinomni.Host.Tests;

/// <summary>
/// The packaged installation's boot contract. A container gets its storage from mounts and its tools from
/// the image, and both fail quietly today: a missing library root makes Import defer forever, and a
/// staging directory owned by another user turns every import into a silent copy. These checks make the
/// mount problem a startup failure that names the path, while keeping a missing optional tool or provider
/// key a warning — an installation that only direct-plays and has no subtitle key must still start.
/// </summary>
public sealed class StartupChecksTests : IDisposable
{
    private readonly string _workingDirectory = Path.Combine(
        Path.GetTempPath(),
        $"cinomni-startup-checks-{Guid.NewGuid():N}");

    public StartupChecksTests() => Directory.CreateDirectory(_workingDirectory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workingDirectory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory must never fail a test run.
        }
    }

    [Fact]
    public void A_storage_root_that_does_not_exist_yet_is_created_and_proved_writable()
    {
        var root = Path.Combine(_workingDirectory, "library");

        StartupChecks.VerifyWritableRoot("Import:LibraryRoot", root);

        Assert.True(Directory.Exists(root));
        // The probe file is transient: what is left behind is an empty root, not an artefact.
        Assert.Empty(Directory.GetFileSystemEntries(root));
    }

    [Fact]
    public void The_check_is_idempotent_across_restarts()
    {
        var root = Path.Combine(_workingDirectory, "downloads");

        StartupChecks.VerifyWritableRoot("Downloads:Sidecar:StagingPath", root);
        File.WriteAllText(Path.Combine(root, "already-here.txt"), "content");
        StartupChecks.VerifyWritableRoot("Downloads:Sidecar:StagingPath", root);

        Assert.Single(Directory.GetFileSystemEntries(root));
    }

    [Fact]
    public void A_root_that_cannot_be_created_fails_with_the_path_and_the_configuration_key()
    {
        // A file where a directory is expected — the shape a mis-mounted volume takes.
        var occupied = Path.Combine(_workingDirectory, "not-a-directory");
        File.WriteAllText(occupied, "content");

        var failure = Assert.Throws<StartupContractException>(
            () => StartupChecks.VerifyWritableRoot("Downloads:Sidecar:StagingPath", occupied));

        Assert.Contains(occupied, failure.Message, StringComparison.Ordinal);
        Assert.Contains("Downloads:Sidecar:StagingPath", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unset_root_fails_naming_the_configuration_key()
    {
        var failure = Assert.Throws<StartupContractException>(
            () => StartupChecks.VerifyWritableRoot("Import:LibraryRoot", "   "));

        Assert.Contains("Import:LibraryRoot", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_runtime_contract_creates_every_storage_root_the_modules_were_configured_with()
    {
        var library = Path.Combine(_workingDirectory, "media", "library");
        var staging = Path.Combine(_workingDirectory, "media", "downloads");
        var transcodes = Path.Combine(_workingDirectory, "media", "transcodes");
        var logger = new RecordingLogger();

        using var services = Container(library, staging, transcodes);
        StartupChecks.VerifyRuntimeContract(services, Configuration([]), logger);

        Assert.True(Directory.Exists(library));
        Assert.True(Directory.Exists(staging));
        Assert.True(Directory.Exists(transcodes));
    }

    [Fact]
    public void A_transcode_root_that_cannot_be_written_warns_rather_than_stopping_the_installation()
    {
        // Scratch space, and the transcode volume is the one root a container commonly gets wrong: it is a
        // named volume seeded from the image, so a custom uid cannot write it. Crashing on it would take
        // the whole interface down for a feature that already fails one session explainably.
        var occupied = Path.Combine(_workingDirectory, "transcodes-is-a-file");
        File.WriteAllText(occupied, "content");
        var logger = new RecordingLogger();

        using var services = Container(
            Path.Combine(_workingDirectory, "library"),
            Path.Combine(_workingDirectory, "downloads"),
            occupied);

        StartupChecks.VerifyRuntimeContract(services, Configuration([]), logger);

        Assert.Contains(
            logger.Records,
            record => record.Level == LogLevel.Warning
                && record.Message.Contains("Playback:TranscodeRoot", StringComparison.Ordinal)
                && record.Message.Contains(occupied, StringComparison.Ordinal));
    }

    [Fact]
    public void A_library_root_that_cannot_be_written_still_stops_the_installation()
    {
        // The other half of the split: the import contract has no degraded mode, so it must not start.
        var occupied = Path.Combine(_workingDirectory, "library-is-a-file");
        File.WriteAllText(occupied, "content");

        using var services = Container(
            occupied,
            Path.Combine(_workingDirectory, "downloads"),
            Path.Combine(_workingDirectory, "transcodes"));

        var failure = Assert.Throws<StartupContractException>(
            () => StartupChecks.VerifyRuntimeContract(services, Configuration([]), new RecordingLogger()));

        Assert.Contains("Import:LibraryRoot", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// What the operator on the other end of that failure actually gets. The check is right to refuse,
    /// and an unhandled exception was the wrong way to say so: the one useful sentence arrived inside a
    /// stack trace and the process died on a signal, which a supervisor reports as a crash rather than
    /// as a refusal to start. This drives the entry point the composition root calls, so the sentence,
    /// the absence of a stack trace and the exit code are all asserted where they are produced.
    /// </summary>
    [Fact]
    public async Task A_violated_contract_ends_the_start_with_one_readable_line_and_a_configuration_exit_code()
    {
        var occupied = Path.Combine(_workingDirectory, "library-is-a-file");
        File.WriteAllText(occupied, "content");
        var logger = new RecordingLogger();

        await using var app = Application(
            logger,
            occupied,
            Path.Combine(_workingDirectory, "downloads"),
            Path.Combine(_workingDirectory, "transcodes"));

        var exitCode = StartupChecks.VerifyRuntimeContract(app);

        // Non-zero is the requirement a supervisor reads: this installation did not start. The value
        // says why — EX_CONFIG, the configuration is wrong — and it is not a signal.
        Assert.NotEqual(0, exitCode);
        Assert.Equal(StartupContractException.ExitCode, exitCode);

        var record = Assert.Single(logger.Records, entry => entry.Level == LogLevel.Critical);
        Assert.Contains("Import:LibraryRoot", record.Message, StringComparison.Ordinal);
        Assert.Contains(occupied, record.Message, StringComparison.Ordinal);
        // The headline is the sentence. An attached exception would put the stack trace back in front
        // of the one line the troubleshooting table tells an operator to read.
        Assert.Null(record.Exception);
    }

    [Fact]
    public async Task A_satisfied_contract_reports_no_exit_code_so_the_start_continues()
    {
        var logger = new RecordingLogger();

        await using var app = Application(
            logger,
            Path.Combine(_workingDirectory, "media", "library"),
            Path.Combine(_workingDirectory, "media", "downloads"),
            Path.Combine(_workingDirectory, "media", "transcodes"));

        Assert.Null(StartupChecks.VerifyRuntimeContract(app));
        Assert.DoesNotContain(logger.Records, record => record.Level == LogLevel.Critical);
    }

    [Fact]
    public async Task Outside_production_the_contract_is_not_checked_and_no_directory_is_created()
    {
        // A developer's checkout inherits container defaults that mean something else on their machine,
        // so the check must not run — and must not report an exit code either, or `dotnet run` would
        // stop on a path that was never theirs.
        var library = Path.Combine(_workingDirectory, "development", "library");
        var logger = new RecordingLogger();

        await using var app = Application(
            logger,
            library,
            Path.Combine(_workingDirectory, "development", "downloads"),
            Path.Combine(_workingDirectory, "development", "transcodes"),
            environment: "Development");

        Assert.Null(StartupChecks.VerifyRuntimeContract(app));
        Assert.False(Directory.Exists(library));
        Assert.Empty(logger.Records);
    }

    /// <summary>
    /// The wiring, driven with the one refusal a single-filesystem developer machine can produce: a
    /// target root that is not there, which the probe reports exactly as it reports any other — by
    /// attempting <c>link()</c> and reading the error. It proves the path through the check (probe fails,
    /// warn naming both keys, never throw) and nothing about which error class caused it; the classes an
    /// operator actually meets are asserted below.
    /// </summary>
    [Fact]
    public void A_probe_that_fails_warns_that_every_import_will_copy_and_names_both_roots()
    {
        var logger = new RecordingLogger();

        StartupChecks.WarnWhenHardlinkNotPossible(
            logger,
            "Downloads:Sidecar:StagingPath",
            _workingDirectory,
            "Import:LibraryRoot",
            Path.Combine(_workingDirectory, "not-mounted"));

        var record = Assert.Single(logger.Records);
        Assert.Equal(LogLevel.Warning, record.Level);
        Assert.Contains("Downloads:Sidecar:StagingPath", record.Message, StringComparison.Ordinal);
        Assert.Contains("Import:LibraryRoot", record.Message, StringComparison.Ordinal);
        Assert.Contains("copy instead of hardlinking", record.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The two container failures the troubleshooting table in DEPLOYMENT.md tells an operator apart, and
    /// they need opposite fixes: <c>EXDEV</c> is the mount layout, <c>EPERM</c> is the uid the two
    /// containers run as. Both have to survive into the one line an operator reads, so the outcome is
    /// driven into the reporting half rather than provoked — neither can be produced on one filesystem
    /// under one account.
    /// </summary>
    [Theory]
    [InlineData(18, "EXDEV")]
    [InlineData(1, "EPERM")]
    [InlineData(13, "EACCES")]
    public void Every_link_failure_an_operator_must_act_on_is_named_in_the_warning(int errorNumber, string expected)
    {
        var logger = new RecordingLogger();

        StartupChecks.ReportHardlinkProbe(
            logger,
            "Downloads:Sidecar:StagingPath",
            "Import:LibraryRoot",
            new HardlinkProbeResult(false, $"link() failed with {Hardlinks.DescribeUnixError(errorNumber)}"));

        var record = Assert.Single(logger.Records);
        Assert.Equal(LogLevel.Warning, record.Level);
        Assert.Contains(expected, record.Message, StringComparison.Ordinal);
        Assert.Contains("copy instead of hardlinking", record.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_probe_that_succeeded_is_reported_as_nothing_at_all()
    {
        var logger = new RecordingLogger();

        StartupChecks.ReportHardlinkProbe(
            logger,
            "Downloads:Sidecar:StagingPath",
            "Import:LibraryRoot",
            new HardlinkProbeResult(true, "a hardlink between the two roots succeeded"));

        Assert.Empty(logger.Records);
    }

    /// <summary>
    /// The genuine cross-filesystem case, which needs two filesystems. Linux has a pair every unprivileged
    /// process can reach — the shared-memory tmpfs and the disk-backed temporary directory — so the real
    /// <c>EXDEV</c> the packaged installation hits when <c>/data/library</c> and <c>/data/downloads</c>
    /// end up on separate mounts is exercised where the product runs. On Windows it is skipped: a second
    /// volume means mounting one, which needs administrative rights this test cannot assume.
    /// </summary>
    [SecondFilesystemFact]
    public void On_Linux_two_roots_on_two_filesystems_report_EXDEV_and_warn()
    {
        var staging = Path.Combine(
            SecondFilesystemFactAttribute.SharedMemory,
            $"cinomni-startup-checks-{Guid.NewGuid():N}");
        var library = Path.Combine("/tmp", $"cinomni-startup-checks-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        Directory.CreateDirectory(library);
        var logger = new RecordingLogger();

        try
        {
            var probe = Hardlinks.Probe(staging, library);

            Assert.False(probe.CanHardlink);
            Assert.Contains("EXDEV", probe.Explanation, StringComparison.Ordinal);

            StartupChecks.WarnWhenHardlinkNotPossible(
                logger,
                "Downloads:Sidecar:StagingPath",
                staging,
                "Import:LibraryRoot",
                library);

            var record = Assert.Single(logger.Records);
            Assert.Equal(LogLevel.Warning, record.Level);
            Assert.Contains("EXDEV", record.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(staging, recursive: true);
            Directory.Delete(library, recursive: true);
        }
    }

    [Fact]
    public void A_source_root_that_cannot_hold_the_probe_file_is_reported_and_never_thrown()
    {
        // Fails closed as a warning: the check must not be the thing that stops a boot, and it must not
        // report a working hardlink because it could not ask.
        var logger = new RecordingLogger();

        StartupChecks.WarnWhenHardlinkNotPossible(
            logger,
            "Downloads:Sidecar:StagingPath",
            Path.Combine(_workingDirectory, "no-such-staging"),
            "Import:LibraryRoot",
            _workingDirectory);

        Assert.Equal(LogLevel.Warning, Assert.Single(logger.Records).Level);
    }

    [Fact]
    public void The_runtime_contract_says_nothing_about_hardlinks_when_the_two_roots_are_one_filesystem()
    {
        // The other half of the promise: a correct installation must not be warned at every start, or
        // the line stops meaning anything. Both roots live under one temporary directory here, which is
        // exactly what the packaged compose file arranges with a single /data mount.
        var logger = new RecordingLogger();

        using var services = Container(
            Path.Combine(_workingDirectory, "media", "library"),
            Path.Combine(_workingDirectory, "media", "downloads"),
            Path.Combine(_workingDirectory, "media", "transcodes"));

        StartupChecks.VerifyRuntimeContract(services, Configuration([]), logger);

        Assert.DoesNotContain(
            logger.Records,
            record => record.Message.Contains("hardlink", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_missing_external_tool_is_a_warning_rather_than_a_failed_boot()
    {
        var logger = new RecordingLogger();
        using var services = Container(
            Path.Combine(_workingDirectory, "library"),
            Path.Combine(_workingDirectory, "downloads"),
            Path.Combine(_workingDirectory, "transcodes"),
            ffprobePath: Path.Combine(_workingDirectory, "no-such-ffprobe"));

        StartupChecks.VerifyRuntimeContract(services, Configuration([]), logger);

        Assert.Contains(
            logger.Records,
            record => record.Level == LogLevel.Warning
                && record.Message.Contains("Import:Ffprobe:BinaryPath", StringComparison.Ordinal));
    }

    [Fact]
    public void An_executable_resolves_by_full_path_and_by_walking_the_search_path()
    {
        var directory = Path.Combine(_workingDirectory, "bin");
        Directory.CreateDirectory(directory);
        var name = OperatingSystem.IsWindows() ? "packaged-tool.exe" : "packaged-tool";
        var full = Path.Combine(directory, name);
        File.WriteAllText(full, string.Empty);

        Assert.True(StartupChecks.CanResolveExecutable(full));
        Assert.True(StartupChecks.CanResolveExecutable("packaged-tool", directory));
        Assert.False(StartupChecks.CanResolveExecutable("packaged-tool", Path.Combine(_workingDirectory, "empty")));
        Assert.False(StartupChecks.CanResolveExecutable(Path.Combine(directory, "absent-tool")));
        Assert.False(StartupChecks.CanResolveExecutable("  ", directory));
    }

    [Fact]
    public void Every_unset_optional_credential_is_reported_by_key_name()
    {
        var logger = new RecordingLogger();

        StartupChecks.WarnWhenCredentialUnset(Configuration([]), logger);

        var warning = Assert.Single(logger.Records);
        Assert.Equal(LogLevel.Warning, warning.Level);
        // The metadata keys can be entered under Settings, so configuration alone cannot call them
        // missing; each provider reports itself at first use instead.
        Assert.DoesNotContain(logger.Records, r => r.Message.Contains("Metadata:", StringComparison.Ordinal));
        Assert.Contains(logger.Records, record => record.Message.Contains("Subtitles:Provider:ApiKey", StringComparison.Ordinal));
    }

    [Fact]
    public void A_configured_credential_value_never_reaches_a_log_record()
    {
        const string Sentinel = "a-value-that-must-never-be-logged";
        var logger = new RecordingLogger();

        StartupChecks.WarnWhenCredentialUnset(
            Configuration(new Dictionary<string, string?>
            {
                ["Metadata:Tmdb:ApiKey"] = Sentinel,
                ["Metadata:Tvdb:ApiKey"] = Sentinel,
                ["Metadata:Tvdb:Pin"] = Sentinel,
                ["Subtitles:Provider:ApiKey"] = Sentinel,
            }),
            logger);

        Assert.Empty(logger.Records);
        Assert.DoesNotContain(
            logger.Records,
            record => record.Message.Contains(Sentinel, StringComparison.Ordinal));
    }

    [Fact]
    public void An_installation_with_no_tunnel_says_so_without_calling_it_a_problem()
    {
        // Running without a tunnel is supported and is what the packaged default does. Reporting it as
        // a warning would train an operator to ignore the line that matters.
        var logger = new RecordingLogger();

        StartupChecks.ReportTorrentEgress(logger, new TunnelOptions(), new SidecarOptions());

        var record = Assert.Single(logger.Records);
        Assert.Equal(LogLevel.Information, record.Level);
        Assert.Contains("no tunnel is configured", record.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_default_policy_of_an_opted_in_installation_is_reported_as_ordinary()
    {
        var logger = new RecordingLogger();

        StartupChecks.ReportTorrentEgress(
            logger,
            new TunnelOptions { Device = "tun0" },
            new SidecarOptions());

        var record = Assert.Single(logger.Records);
        Assert.Equal(LogLevel.Information, record.Level);
        Assert.Contains("tun0", record.Message, StringComparison.Ordinal);
        Assert.Contains("Block", record.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(TunnelLossPolicy.PauseAndAlert)]
    [InlineData(TunnelLossPolicy.Ignore)]
    public void A_policy_weaker_than_the_default_is_a_warning_that_names_the_mode(TunnelLossPolicy policy)
    {
        // The reconciliation between "configurable" and "fails closed": anything below Block is
        // allowed and can never be silent. This is the line an audit of a leaking installation reads.
        var logger = new RecordingLogger();

        StartupChecks.ReportTorrentEgress(
            logger,
            new TunnelOptions { Device = "tun0", LossPolicy = policy },
            new SidecarOptions());

        var record = Assert.Single(logger.Records);
        Assert.Equal(LogLevel.Warning, record.Level);
        Assert.Contains(policy.ToString(), record.Message, StringComparison.Ordinal);
        Assert.Contains("weaker than the Block default", record.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_control_credential_is_reported_by_presence_and_never_by_value()
    {
        const string Sentinel = "a-control-token-that-must-never-be-logged";
        var logger = new RecordingLogger();

        StartupChecks.ReportTorrentEgress(
            logger,
            new TunnelOptions { Device = "tun0" },
            new SidecarOptions { ControlToken = Sentinel });

        var record = Assert.Single(logger.Records);
        Assert.Contains("Sidecar control credential: set", record.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Sentinel, record.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reporting_hardware_capabilities_publishes_the_probe_result_to_the_cache()
    {
        var services = new ServiceCollection();
        services.AddSingleton<HardwareCapabilitiesCache>();
        services.AddSingleton<IHardwareCapabilityProbe>(
            new FakeHardwareCapabilityProbe(new HardwareCapabilities([EncoderBackend.Nvenc])));
        await using var provider = services.BuildServiceProvider();

        await StartupChecks.ReportHardwareCapabilitiesAsync(provider, new RecordingLogger());

        var cache = provider.GetRequiredService<HardwareCapabilitiesCache>();
        Assert.Equal([EncoderBackend.Nvenc], cache.Current.AvailableBackends);
    }

    [Fact]
    public async Task An_installation_with_no_hardware_backend_publishes_an_empty_result_and_says_so()
    {
        var services = new ServiceCollection();
        services.AddSingleton<HardwareCapabilitiesCache>();
        services.AddSingleton<IHardwareCapabilityProbe>(new FakeHardwareCapabilityProbe(new HardwareCapabilities([])));
        await using var provider = services.BuildServiceProvider();
        var logger = new RecordingLogger();

        await StartupChecks.ReportHardwareCapabilitiesAsync(provider, logger);

        Assert.Empty(provider.GetRequiredService<HardwareCapabilitiesCache>().Current.AvailableBackends);
        var record = Assert.Single(logger.Records);
        Assert.Equal(LogLevel.Information, record.Level);
        Assert.Contains("software x264", record.Message, StringComparison.Ordinal);
    }

    private sealed class FakeHardwareCapabilityProbe(HardwareCapabilities result) : IHardwareCapabilityProbe
    {
        public Task<HardwareCapabilities> ProbeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(result);
    }

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    /// <summary>The option singletons the check reads, exactly as the module adapters register them.</summary>
    private static ServiceProvider Container(
        string libraryRoot,
        string stagingPath,
        string transcodeRoot,
        string ffprobePath = "ffprobe")
    {
        var services = new ServiceCollection();
        Register(services, libraryRoot, stagingPath, transcodeRoot, ffprobePath);
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// The same options behind a real <see cref="WebApplication"/>, because the entry point the
    /// composition root calls reads the environment and the logger factory from one. Nothing is started
    /// and nothing binds a port: the check is driven directly, exactly as <c>Program</c> drives it.
    /// </summary>
    private static WebApplication Application(
        ILogger logger,
        string libraryRoot,
        string stagingPath,
        string transcodeRoot,
        string environment = "Production")
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            EnvironmentName = environment,
        });

        Register(builder.Services, libraryRoot, stagingPath, transcodeRoot, "ffprobe");
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new RecordingLoggerProvider(logger));

        return builder.Build();
    }

    private static void Register(
        IServiceCollection services,
        string libraryRoot,
        string stagingPath,
        string transcodeRoot,
        string ffprobePath)
    {
        services.AddSingleton(new ImportOptions { LibraryRoot = libraryRoot });
        services.AddSingleton(new SidecarOptions { StagingPath = stagingPath });
        services.AddSingleton(new PlaybackOptions { TranscodeRoot = transcodeRoot });
        services.AddSingleton(new FfprobeOptions { BinaryPath = ffprobePath });
        services.AddSingleton(new FfmpegEncoderOptions());
        // No tunnel, which is the packaged default and what every installation without one has.
        services.AddSingleton(new TunnelOptions());
    }

    private sealed record LogRecord(LogLevel Level, string Message, Exception? Exception);

    private sealed class RecordingLogger : ILogger
    {
        public List<LogRecord> Records { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Records.Add(new LogRecord(logLevel, formatter(state, exception), exception));
    }

    /// <summary>
    /// Hands the recording logger to the one category under test and swallows everything else, so a
    /// framework message cannot be mistaken for a line the startup checks wrote.
    /// </summary>
    private sealed class RecordingLoggerProvider(ILogger logger) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) =>
            string.Equals(categoryName, typeof(StartupChecks).FullName, StringComparison.Ordinal)
                ? logger
                : NullLogger.Instance;

        public void Dispose()
        {
        }
    }
}
