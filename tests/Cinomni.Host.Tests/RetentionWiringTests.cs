using Cinomni.Decision.Application;
using Cinomni.Discovery.Application;
using Cinomni.Downloads.Application;
using Cinomni.Identity.Application;
using Cinomni.Kernel.Messaging;
using Cinomni.Metadata.Application;
using Cinomni.Notifications.Application;
using Cinomni.Operations.Messaging;
using Cinomni.Operations.Retention;
using Cinomni.Playback.Application;
using Cinomni.ReleaseParsing.Application;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cinomni.Host.Tests;

/// <summary>
/// Pins the retention wiring at the composition root. Nothing connects and no migration runs: this
/// reads back what the Host registers.
/// <para>
/// A purge that is written but never scheduled looks exactly like a purge that works, until the
/// database fills up months later — so the job registrations, the command names and the handlers are
/// asserted together, and the configuration keys are asserted by round-tripping real values through
/// the same binder <c>Program</c> uses.
/// </para>
/// </summary>
public sealed class RetentionWiringTests
{
    /// <summary>Every retention job the installation is expected to run, and the command it enqueues.</summary>
    private static readonly (string Job, string Command)[] ExpectedJobs =
    [
        ("operations.retention", "operations.purge-retention"),
        ("discovery.retention", "discovery.purge-searches"),
        ("decision.retention", "decision.purge-evaluations"),
        ("metadata.retention", "metadata.purge-snapshots"),
        ("parsing.retention", "parsing.purge-parsed-releases"),
        ("notifications.retention", "notifications.purge"),
        ("identity.retention", "identity.purge-sessions"),
        ("playback.retention", "playback.purge-sessions"),
        ("downloads.retention", "downloads.trim-checkpoints"),
    ];

    [Fact]
    public void Every_retention_job_is_registered_with_its_command()
    {
        var provider = Compose(DefaultConfiguration());

        var jobs = provider.GetServices<ScheduledJobRegistration>()
            .ToDictionary(job => job.Name, job => job.CommandName, StringComparer.Ordinal);

        foreach (var (job, command) in ExpectedJobs)
        {
            Assert.True(jobs.ContainsKey(job), $"No scheduled job '{job}' is registered.");
            Assert.Equal(command, jobs[job]);
        }
    }

    /// <summary>
    /// A job whose command has no registered type or no handler fails at run time inside a background
    /// worker, where nobody is looking. This covers every scheduled job, not only the retention ones.
    /// </summary>
    [Fact]
    public void Every_scheduled_job_resolves_a_registered_command_and_a_handler()
    {
        var provider = Compose(DefaultConfiguration());
        var registry = provider.GetRequiredService<IMessageTypeRegistry>();

        using var scope = provider.CreateScope();

        foreach (var job in provider.GetServices<ScheduledJobRegistration>())
        {
            var commandType = registry.Resolve(job.CommandName);
            var handlerType = typeof(ICommandHandler<>).MakeGenericType(commandType);

            Assert.NotNull(scope.ServiceProvider.GetService(handlerType));
        }
    }

    /// <summary>Every documented key really reaches its owner: a typo here would silently keep the default.</summary>
    [Fact]
    public void Configured_windows_reach_every_owner()
    {
        var provider = Compose(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Retention:OutboxRetention"] = "3.00:00:00",
                ["Retention:CompletedCommandRetention"] = "7.00:00:00",
                ["Retention:FailedCommandRetention"] = "40.00:00:00",
                ["Retention:BatchSize"] = "250",
                ["Retention:Interval"] = "02:00:00",
                ["Retention:Discovery:SearchRetention"] = "11.00:00:00",
                ["Retention:Decision:EvaluationRetention"] = "12.00:00:00",
                ["Retention:Metadata:SnapshotRetention"] = "13.00:00:00",
                ["Retention:ReleaseParsing:ParseRetention"] = "14.00:00:00",
                ["Retention:Notifications:NotificationRetention"] = "15.00:00:00",
                ["Retention:Identity:SessionGrace"] = "16.00:00:00",
                ["Retention:Playback:SessionRetention"] = "17.00:00:00",
                ["Retention:Downloads:CheckpointRetention"] = "18.00:00:00",
            })
            .Build());

        var platform = provider.GetRequiredService<RetentionOptions>();
        Assert.Equal(TimeSpan.FromDays(3), platform.OutboxRetention);
        Assert.Equal(TimeSpan.FromDays(7), platform.CompletedCommandRetention);
        Assert.Equal(TimeSpan.FromDays(40), platform.FailedCommandRetention);
        Assert.Equal(250, platform.BatchSize);
        Assert.Equal(TimeSpan.FromHours(2), platform.Interval);

        Assert.Equal(
            TimeSpan.FromDays(11), provider.GetRequiredService<SearchRetentionOptions>().SearchRetention);
        Assert.Equal(
            TimeSpan.FromDays(12), provider.GetRequiredService<DecisionRetentionOptions>().EvaluationRetention);
        Assert.Equal(
            TimeSpan.FromDays(13), provider.GetRequiredService<MetadataOptions>().SnapshotRetention);
        Assert.Equal(
            TimeSpan.FromDays(14), provider.GetRequiredService<ReleaseParsingRetentionOptions>().ParseRetention);
        Assert.Equal(
            TimeSpan.FromDays(15),
            provider.GetRequiredService<NotificationRetentionOptions>().NotificationRetention);
        Assert.Equal(
            TimeSpan.FromDays(16), provider.GetRequiredService<SessionRetentionOptions>().SessionGrace);
        Assert.Equal(
            TimeSpan.FromDays(17), provider.GetRequiredService<PlaybackOptions>().SessionRetention);
        Assert.Equal(
            TimeSpan.FromDays(18), provider.GetRequiredService<DownloadRetentionOptions>().CheckpointRetention);
    }

    /// <summary>
    /// The load-bearing invariant, enforced where an operator finds out immediately. A completed
    /// command row is its own idempotency record: freeing it before the outbox message that could
    /// re-enqueue it would let finished work run twice.
    /// </summary>
    [Fact]
    public void A_completed_command_window_inside_the_outbox_window_stops_startup()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Retention:OutboxRetention"] = "60.00:00:00",
                ["Retention:CompletedCommandRetention"] = "30.00:00:00",
            })
            .Build();

        var failure = Assert.Throws<InvalidOperationException>(() => Compose(configuration));

        Assert.Contains("CompletedCommandRetention", failure.Message, StringComparison.Ordinal);
        Assert.Contains("OutboxRetention", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_non_positive_module_window_stops_startup()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Retention:Discovery:SearchRetention"] = "00:00:00",
            })
            .Build();

        var failure = Assert.Throws<InvalidOperationException>(() => Compose(configuration));

        Assert.Contains("SearchRetention", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Metadata keeps its window on the refresh profile rather than in its own options type, which is
    /// exactly how it could have ended up as the one owner nobody validated. A zero window would retire
    /// a snapshot the moment it is superseded — including one Catalog's queued attach command is about
    /// to resolve by id.
    /// </summary>
    [Fact]
    public void A_non_positive_metadata_window_stops_startup()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Retention:Metadata:SnapshotRetention"] = "00:00:00",
            })
            .Build();

        var failure = Assert.Throws<InvalidOperationException>(() => Compose(configuration));

        Assert.Contains("SnapshotRetention", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A zero cadence is not "run often": the scheduler would set the next due instant to now on every
    /// tick and enqueue a fresh purge command every few seconds, for ever.
    /// </summary>
    [Fact]
    public void A_zero_metadata_interval_stops_startup()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Retention:Metadata:Interval"] = "00:00:00",
            })
            .Build();

        var failure = Assert.Throws<InvalidOperationException>(() => Compose(configuration));

        Assert.Contains("Retention:Metadata:Interval", failure.Message, StringComparison.Ordinal);
    }

    private static IConfiguration DefaultConfiguration() => new ConfigurationBuilder().Build();

    /// <summary>
    /// Composes the real composition root — the same <c>AddCinomniModules</c> the Host calls — so a
    /// module added there without its retention callback is visible here instead of passing against a
    /// private copy of the list. Registration is lazy: no connection is opened and no migration runs.
    /// </summary>
    private static ServiceProvider Compose(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.None));
        services.AddCinomniModules(
            configuration, "Host=localhost;Database=cinomni_unused;Username=none;Password=none");

        return services.BuildServiceProvider();
    }
}
