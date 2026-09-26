using Cinomni.Operations.Backup;
using Cinomni.Operations.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cinomni.Host.Tests;

/// <summary>
/// Pins the backup wiring at the composition root. Nothing connects and no migration runs: this reads
/// back what the Host registers.
/// <para>
/// Two things here are worth more than they look. The <b>schema list</b> is the manifest's contents, so a
/// module whose context is never registered would be dumped by <c>pg_dump</c> — which takes the whole
/// database — and then silently skipped by the compatibility gate that is supposed to refuse a mismatched
/// restore. And the <b>root separation</b> is what keeps a file full of delivery-channel URLs and password
/// verifiers out of a directory the media surfaces serve from.
/// </para>
/// </summary>
public sealed class BackupWiringTests
{
    /// <summary>
    /// Every schema a backup manifest must record, and the one deliberate absence: RealTime owns no
    /// schema and no context, so it has nothing to record.
    /// </summary>
    private static readonly string[] ExpectedSchemas =
    [
        "operations", "identity", "catalog", "metadata", "monitoring", "discovery", "parsing",
        "decision", "acquisition", "downloads", "import", "library", "playback", "subtitles",
        "requests", "notifications",
    ];

    [Fact]
    public void Every_schema_in_the_installation_is_covered_by_the_manifest()
    {
        var provider = Compose(DefaultConfiguration());

        var covered = provider.GetServices<SchemaVersionSource>().Select(source => source.Schema).ToArray();

        Assert.Equal(ExpectedSchemas.Length, covered.Length);
        Assert.Equal(ExpectedSchemas.Order(StringComparer.Ordinal), covered.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// A backup runs on a worker of its own and never through the command queue. One command worker
    /// dispatches its batch sequentially, so a dump on it would stall every other module's queued work —
    /// the download checkpoint included — for as long as the dump takes.
    /// </summary>
    [Fact]
    public void The_backup_worker_is_registered_and_nothing_backup_shaped_reaches_the_command_queue()
    {
        var provider = Compose(DefaultConfiguration());

        Assert.Single(provider.GetServices<IHostedService>().OfType<BackupHostedService>());

        Assert.DoesNotContain(
            provider.GetServices<ScheduledJobRegistration>(),
            registration => registration.Name.Contains("backup", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            provider.GetServices<MessageTypeRegistration>(),
            registration => registration.Name.Contains("backup", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Turning the schedule off must not remove the ability to take a backup by hand — that is exactly
    /// when an operator needs one, right before an upgrade.
    /// </summary>
    [Fact]
    public void Disabling_the_schedule_removes_the_worker_and_keeps_the_service()
    {
        var provider = Compose(Configuration(new Dictionary<string, string?>
        {
            ["Backup:Enabled"] = "false",
        }));

        Assert.Empty(provider.GetServices<IHostedService>().OfType<BackupHostedService>());

        using var scope = provider.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetService<BackupService>());
        Assert.NotNull(scope.ServiceProvider.GetService<BackupJournal>());
    }

    [Fact]
    public void Every_documented_key_reaches_the_backup_options()
    {
        var root = Path.Combine(Path.GetTempPath(), "cinomni-backup-wiring");

        var provider = Compose(Configuration(new Dictionary<string, string?>
        {
            ["Backup:Enabled"] = "true",
            ["Backup:Root"] = root,
            ["Backup:KeepCount"] = "3",
            ["Backup:Interval"] = "06:00:00",
            ["Backup:Timeout"] = "00:45:00",
            ["Backup:PgDumpPath"] = "/usr/lib/postgresql/16/bin/pg_dump",
            ["Backup:PgRestorePath"] = "/usr/lib/postgresql/16/bin/pg_restore",
        }));

        var options = provider.GetRequiredService<BackupOptions>();
        Assert.True(options.Enabled);
        Assert.Equal(Path.GetFullPath(root), options.Root);
        Assert.Equal(3, options.KeepCount);
        Assert.Equal(TimeSpan.FromHours(6), options.Interval);
        Assert.Equal(TimeSpan.FromMinutes(45), options.Timeout);
        Assert.Equal("/usr/lib/postgresql/16/bin/pg_dump", options.PgDumpPath);
        Assert.Equal("/usr/lib/postgresql/16/bin/pg_restore", options.PgRestorePath);
    }

    [Fact]
    public void A_configuration_that_would_empty_the_backup_directory_stops_startup()
    {
        var failure = Assert.Throws<InvalidOperationException>(() => Compose(Configuration(
            new Dictionary<string, string?> { ["Backup:KeepCount"] = "0" })));

        Assert.Contains("KeepCount", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The startup refusal, driven through the real composition root so the storage roots come from the
    /// same options the modules were configured with rather than from a copy.
    /// </summary>
    [Fact]
    public void A_backup_root_inside_the_library_stops_startup_naming_both_keys()
    {
        var library = Path.Combine(Path.GetTempPath(), "cinomni-wiring-library");

        var provider = Compose(Configuration(new Dictionary<string, string?>
        {
            ["Import:LibraryRoot"] = library,
            ["Backup:Root"] = Path.Combine(library, "backups"),
        }));

        var failure = Assert.Throws<InvalidOperationException>(
            () => BackupStartup.VerifyRootIsSeparate(provider));

        Assert.Contains("Import:LibraryRoot", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Backup:Root", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_packaged_defaults_keep_the_backup_root_out_of_every_storage_root()
    {
        var provider = Compose(Configuration(new Dictionary<string, string?>
        {
            ["Backup:Root"] = "/data/backups",
            ["Import:LibraryRoot"] = "/data/library",
            ["Downloads:Sidecar:StagingPath"] = "/data/downloads",
            ["Playback:TranscodeRoot"] = "/data/transcodes",
        }));

        BackupStartup.VerifyRootIsSeparate(provider);
    }

    /// <summary>A missing tool warns and keeps the installation up; it must never take the interface down.</summary>
    [Fact]
    public void A_missing_backup_tool_is_a_warning_rather_than_a_failed_boot()
    {
        var provider = Compose(Configuration(new Dictionary<string, string?>
        {
            ["Backup:Root"] = Path.Combine(Path.GetTempPath(), "cinomni-wiring-backups"),
            ["Backup:PgDumpPath"] = Path.Combine(Path.GetTempPath(), "no-such-pg-dump"),
        }));
        var logger = new CollectingLogger();

        BackupStartup.ReportBackupContract(provider, logger);

        Assert.Contains(
            logger.Messages,
            message => message.Contains("Backup:PgDumpPath", StringComparison.Ordinal));
    }

    private static IConfiguration DefaultConfiguration() => new ConfigurationBuilder().Build();

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    /// <summary>
    /// Composes the real composition root — the same <c>AddCinomniModules</c> the Host calls — so a
    /// context added there without its backup schema source is visible here. Registration is lazy: no
    /// connection is opened and no migration runs.
    /// </summary>
    private static ServiceProvider Compose(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.None));
        services.AddCinomniModules(configuration, HostComposition.UnusableConnectionString);

        return services.BuildServiceProvider();
    }

    private sealed class CollectingLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
