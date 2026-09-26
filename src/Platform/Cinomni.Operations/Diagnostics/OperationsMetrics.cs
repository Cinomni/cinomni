using System.Diagnostics;
using System.Diagnostics.Metrics;
using Cinomni.Kernel.Diagnostics;

namespace Cinomni.Operations.Diagnostics;

/// <summary>
/// What an operator needs to know about the spine every cross-module flow runs on: is the outbox
/// keeping up, is the command queue draining, and is anything retrying forever.
/// <para>
/// The instruments are static for the same reason <c>NetworkGuardMetrics</c>'s are: an exporter
/// subscribes to a meter <em>name</em>, not to a container, and the relay, the queue and the processor
/// are all scoped services constructed per batch. Threading an instrument through their constructors
/// would buy nothing and would make every test that resolves one need a metrics registration.
/// </para>
/// <para>
/// Every tag here is bounded: a registered event name, a registered command name, a registered job
/// name, a queue state, or a fixed outcome word. No payload, no idempotency key and no identifier ever
/// becomes a label — an idempotency key is derived from domain identities and would make the
/// cardinality of these series unbounded.
/// </para>
/// </summary>
public static class OperationsMetrics
{
    private static readonly Meter Meter = new(CinomniTelemetry.MeterName);

    private static readonly KeyValuePair<string, object?> ModuleTag =
        new(CinomniTelemetry.Tags.Module, CinomniTelemetry.Modules.Operations);

    private static readonly Counter<long> OutboxPublished = Meter.CreateCounter<long>(
        "cinomni.outbox.published",
        unit: "{message}",
        description: "Outbox messages dispatched to their handlers, by event name.");

    private static readonly Counter<long> OutboxFailures = Meter.CreateCounter<long>(
        "cinomni.outbox.failures",
        unit: "{message}",
        description: "Outbox deliveries a handler failed; only that message was rolled back and it is retried later.");

    private static readonly Counter<long> OutboxDeadLettered = Meter.CreateCounter<long>(
        "cinomni.outbox.dead_lettered",
        unit: "{message}",
        description: "Outbox messages set aside after failing every delivery attempt. Each one needs an operator.");

    private static readonly Histogram<double> OutboxRelayDuration = Meter.CreateHistogram<double>(
        "cinomni.outbox.relay.duration",
        unit: "s",
        description: "Time spent dispatching one outbox message to its handlers.");

    private static readonly Histogram<double> OutboxRelayLag = Meter.CreateHistogram<double>(
        "cinomni.outbox.relay.lag",
        unit: "s",
        description: "Delay between an event occurring and the relay dispatching it. The real backlog signal.");

    private static readonly Counter<long> CommandExecuted = Meter.CreateCounter<long>(
        "cinomni.command.executed",
        unit: "{command}",
        description: "Command executions by name and outcome (completed, retried, failed).");

    private static readonly Histogram<double> CommandDuration = Meter.CreateHistogram<double>(
        "cinomni.command.duration",
        unit: "s",
        description: "Time one command handler took, successful or not.");

    private static readonly Counter<long> CommandDropped = Meter.CreateCounter<long>(
        "cinomni.command.dropped",
        unit: "{command}",
        description: "Commands not enqueued because their idempotency key was already spent.");

    private static readonly Counter<long> JobRuns = Meter.CreateCounter<long>(
        "cinomni.job.runs",
        unit: "{run}",
        description: "Scheduled job ticks that fired, by job name and outcome.");

    /// <summary>
    /// The queue depths, published from a cached snapshot. A metric callback must never open a
    /// transaction — an exporter scrapes on its own schedule and would otherwise put unbounded database
    /// load behind a configuration value nobody in this process controls. <see cref="QueueDepthSampler"/>
    /// refreshes the snapshot on a fixed cadence instead; until it has run once, these report nothing.
    /// </summary>
    static OperationsMetrics()
    {
        Meter.CreateObservableGauge(
            "cinomni.outbox.pending",
            ObserveOutboxPending,
            unit: "{message}",
            description: "Outbox messages written but not yet dispatched.");

        Meter.CreateObservableGauge(
            "cinomni.outbox.oldest_pending.age",
            ObserveOldestPendingAge,
            unit: "s",
            description: "Age of the oldest undispatched outbox message. Zero when the outbox is empty.");

        Meter.CreateObservableGauge(
            "cinomni.command.queue.depth",
            ObserveCommandDepth,
            unit: "{command}",
            description: "Commands in the queue by lifecycle state.");
    }

    /// <summary>The most recent sample, or <see langword="null"/> before the sampler's first pass.</summary>
    private static volatile QueueDepthSnapshot? _snapshot;

    /// <summary>Publishes a fresh sample for the observable gauges above.</summary>
    public static void PublishQueueDepths(QueueDepthSnapshot snapshot) => _snapshot = snapshot;

    /// <summary>Records one dispatched outbox message and how far behind the relay was.</summary>
    public static void RecordOutboxPublished(string eventName, TimeSpan duration, TimeSpan lag)
    {
        var tags = new TagList { ModuleTag, new(CinomniTelemetry.Tags.EventName, eventName) };
        OutboxPublished.Add(1, tags);
        OutboxRelayDuration.Record(duration.TotalSeconds, tags);
        // A clock stepping backwards must not produce a negative bucket.
        OutboxRelayLag.Record(Math.Max(0d, lag.TotalSeconds), tags);
    }

    /// <summary>Records a delivery a handler failed. The message stays unpublished and is retried after its backoff.</summary>
    public static void RecordOutboxBatchFailure() => OutboxFailures.Add(1, ModuleTag);

    /// <summary>Records a message set aside after its last failed delivery, by event type.</summary>
    public static void RecordOutboxDeadLettered(string eventType) =>
        OutboxDeadLettered.Add(1, new TagList { ModuleTag, new(CinomniTelemetry.Tags.EventName, eventType) });

    /// <summary>Records one command attempt and how it ended.</summary>
    public static void RecordCommandExecuted(string commandName, string outcome, TimeSpan duration)
    {
        var tags = new TagList
        {
            ModuleTag,
            new(CinomniTelemetry.Tags.CommandName, commandName),
            new(CinomniTelemetry.Tags.Outcome, outcome),
        };

        CommandExecuted.Add(1, tags);
        CommandDuration.Record(duration.TotalSeconds, tags);
    }

    /// <summary>
    /// Records a command that was never enqueued because its key was spent. Visible on purpose: a
    /// dropped command writes no row, publishes no event and fails nothing, so without this the only
    /// symptom is work that silently never happens.
    /// </summary>
    public static void RecordCommandDropped(string commandName) =>
        CommandDropped.Add(
            1,
            ModuleTag,
            new KeyValuePair<string, object?>(CinomniTelemetry.Tags.CommandName, commandName),
            new KeyValuePair<string, object?>(CinomniTelemetry.Tags.Outcome, CinomniTelemetry.Outcomes.Dropped));

    /// <summary>Records a scheduled job tick that fired.</summary>
    public static void RecordJobRun(string jobName, string outcome) =>
        JobRuns.Add(
            1,
            ModuleTag,
            new KeyValuePair<string, object?>(CinomniTelemetry.Tags.JobName, jobName),
            new KeyValuePair<string, object?>(CinomniTelemetry.Tags.Outcome, outcome));

    private static IEnumerable<Measurement<long>> ObserveOutboxPending() =>
        _snapshot is { } snapshot ? [new Measurement<long>(snapshot.OutboxPending, ModuleTag)] : [];

    private static IEnumerable<Measurement<double>> ObserveOldestPendingAge() =>
        _snapshot is { } snapshot
            ? [new Measurement<double>(Math.Max(0d, snapshot.OldestOutboxAge.TotalSeconds), ModuleTag)]
            : [];

    private static IEnumerable<Measurement<long>> ObserveCommandDepth() =>
        _snapshot is { } snapshot
            ? snapshot.CommandsByState.Select(entry => new Measurement<long>(
                entry.Value,
                ModuleTag,
                new KeyValuePair<string, object?>(CinomniTelemetry.Tags.State, entry.Key)))
            : [];
}

/// <summary>One sample of the spine's backlog. Immutable so a reader never sees a half-written pair.</summary>
/// <param name="OutboxPending">Messages written but not yet dispatched.</param>
/// <param name="OldestOutboxAge">Age of the oldest of them; <see cref="TimeSpan.Zero"/> when there are none.</param>
/// <param name="CommandsByState">Row counts per command lifecycle state.</param>
public sealed record QueueDepthSnapshot(
    long OutboxPending,
    TimeSpan OldestOutboxAge,
    IReadOnlyDictionary<string, long> CommandsByState);
