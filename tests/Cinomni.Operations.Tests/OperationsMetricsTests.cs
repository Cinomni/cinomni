using System.Diagnostics.Metrics;
using Cinomni.Kernel.Diagnostics;
using Cinomni.Kernel.Messaging;
using Cinomni.Kernel.Results;
using Cinomni.Operations.Diagnostics;
using Cinomni.Operations.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cinomni.Operations.Tests;

/// <summary>
/// The spine's own numbers, observed the way an exporter observes them — with a plain BCL
/// <see cref="MeterListener"/>, so no test-only telemetry package is introduced and nothing here can
/// pass against a mock that the real collector would not see.
/// <para>
/// What is asserted is that the questions an operator actually asks have an answer: is the outbox
/// keeping up (published count and relay lag), is anything retrying (command outcome), and how deep is
/// the backlog right now (the sampled gauges). Also asserted: no tag carries an identifier, because a
/// label with unbounded cardinality is how a self-hosted installation gets a metrics database it
/// cannot afford.
/// </para>
/// </summary>
public sealed class OperationsMetricsTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync()
    {
        _provider = await OperationsTestHost.CreateAsync("cinomni_test_operations_metrics", services =>
        {
            services.AddIntegrationEvent<Measured>("test.metrics.measured");
            services.AddScoped<IEventHandler<Measured>, MeasuredHandler>();
            services.AddCommand<AlwaysFails>("test.metrics.always-fails");
            services.AddScoped<ICommandHandler<AlwaysFails>, AlwaysFailsHandler>();
        });
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task Relaying_a_message_records_its_count_and_how_far_behind_the_relay_was()
    {
        using var recorder = new MetricRecorder();

        await using (var scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IEventBus>().PublishAsync(new Measured());
        }

        await using (var scope = _provider.CreateAsyncScope())
        {
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<OutboxRelay>().ProcessBatchAsync());
        }

        var published = Assert.Single(
            recorder.Longs("cinomni.outbox.published", CinomniTelemetry.Tags.EventName, "test.metrics.measured"));
        Assert.Equal(1, published.Value);

        // Lag is the signal that says "the relay is behind", which a published count alone never does.
        var lag = Assert.Single(
            recorder.Doubles("cinomni.outbox.relay.lag", CinomniTelemetry.Tags.EventName, "test.metrics.measured"));
        Assert.True(lag.Value >= 0);

        Assert.Single(
            recorder.Doubles("cinomni.outbox.relay.duration", CinomniTelemetry.Tags.EventName, "test.metrics.measured"));
    }

    [Fact]
    public async Task A_command_that_fails_below_its_attempt_limit_is_recorded_as_retried()
    {
        using var recorder = new MetricRecorder();

        await using (var scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ICommandQueue>()
                .EnqueueAsync(new AlwaysFails(), "metrics:always-fails");
        }

        await using (var scope = _provider.CreateAsyncScope())
        {
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<CommandProcessor>().ProcessBatchAsync());
        }

        var executed = Assert.Single(recorder.Longs(
            "cinomni.command.executed", CinomniTelemetry.Tags.CommandName, "test.metrics.always-fails"));
        Assert.Equal(CinomniTelemetry.Outcomes.Retried, executed.Tags[CinomniTelemetry.Tags.Outcome]);

        // The handler's error text can quote whatever input produced it. It belongs in the command row,
        // never in a label.
        Assert.DoesNotContain(
            executed.Tags,
            tag => tag.Value is string text && text.Contains("boom", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_command_whose_key_is_already_spent_is_counted_rather_than_lost()
    {
        using var recorder = new MetricRecorder();

        await using (var scope = _provider.CreateAsyncScope())
        {
            var queue = scope.ServiceProvider.GetRequiredService<ICommandQueue>();
            Assert.True(await queue.EnqueueAsync(new AlwaysFails(), "metrics:duplicate"));
            Assert.False(await queue.EnqueueAsync(new AlwaysFails(), "metrics:duplicate"));
        }

        var dropped = Assert.Single(recorder.Longs(
            "cinomni.command.dropped", CinomniTelemetry.Tags.CommandName, "test.metrics.always-fails"));
        Assert.Equal(CinomniTelemetry.Outcomes.Dropped, dropped.Tags[CinomniTelemetry.Tags.Outcome]);
    }

    [Fact]
    public async Task Sampling_publishes_the_backlog_the_gauges_report()
    {
        await using (var scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IEventBus>().PublishAsync(new Measured());
            await scope.ServiceProvider.GetRequiredService<ICommandQueue>()
                .EnqueueAsync(new AlwaysFails(), "metrics:depth");
        }

        // The sampler is driven directly: it is a hosted service, and hosted services do not run in a
        // test service provider.
        QueueDepthSnapshot snapshot;
        await using (var scope = _provider.CreateAsyncScope())
        {
            snapshot = await scope.ServiceProvider.GetRequiredService<QueueDepthSampler>().SampleAsync();
        }

        Assert.Equal(1, snapshot.OutboxPending);
        Assert.True(snapshot.OldestOutboxAge >= TimeSpan.Zero);
        Assert.Equal(1, snapshot.CommandsByState["Queued"]);
        // Every state is published even at zero, so a dashboard reads "none" rather than "no data".
        Assert.Equal(0, snapshot.CommandsByState["Failed"]);

        using var recorder = new MetricRecorder();
        recorder.CollectObservable();

        var pending = Assert.Single(recorder.Longs("cinomni.outbox.pending"));
        Assert.Equal(1, pending.Value);
        Assert.Contains(
            recorder.Longs("cinomni.command.queue.depth"),
            measurement => (string?)measurement.Tags[CinomniTelemetry.Tags.State] == "Queued" && measurement.Value == 1);
    }

    private sealed record Measured : DomainEvent
    {
        public override string IdempotencyKey => "metrics:measured";
    }

    private sealed record AlwaysFails : ICommand;

    private sealed class MeasuredHandler : IEventHandler<Measured>
    {
        public Task HandleAsync(Measured domainEvent, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class AlwaysFailsHandler : ICommandHandler<AlwaysFails>
    {
        public Task<Result> HandleAsync(AlwaysFails command, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Failure(new Error("test.boom", "boom")));
    }

    /// <summary>Captures every measurement published on the Cinomni meter while it is alive.</summary>
    private sealed class MetricRecorder : IDisposable
    {
        private readonly MeterListener _listener;
        private readonly List<Measurement<long>> _longs = [];
        private readonly List<Measurement<double>> _doubles = [];
        private readonly Lock _gate = new();

        public MetricRecorder()
        {
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name == CinomniTelemetry.MeterName)
                    {
                        listener.EnableMeasurementEvents(instrument);
                    }
                },
            };

            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                lock (_gate)
                {
                    _longs.Add(new Measurement<long>(instrument.Name, value, tags));
                }
            });

            _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            {
                lock (_gate)
                {
                    _doubles.Add(new Measurement<double>(instrument.Name, value, tags));
                }
            });

            _listener.Start();
        }

        /// <summary>Forces one collection pass over the observable instruments.</summary>
        public void CollectObservable() => _listener.RecordObservableInstruments();

        /// <summary>
        /// One instrument's measurements, optionally narrowed to one tag value. The meter is
        /// process-wide by design — an exporter subscribes to a name, not to a container — so a test
        /// class running beside another must select its own series rather than assume it is alone.
        /// </summary>
        public IReadOnlyList<Measurement<long>> Longs(string instrument, string? tagKey = null, string? tagValue = null)
        {
            lock (_gate)
            {
                return _longs.Where(m => m.Instrument == instrument && Matches(m.Tags, tagKey, tagValue)).ToList();
            }
        }

        public IReadOnlyList<Measurement<double>> Doubles(string instrument, string? tagKey = null, string? tagValue = null)
        {
            lock (_gate)
            {
                return _doubles.Where(m => m.Instrument == instrument && Matches(m.Tags, tagKey, tagValue)).ToList();
            }
        }

        private static bool Matches(IReadOnlyDictionary<string, object?> tags, string? key, string? value) =>
            key is null || (tags.TryGetValue(key, out var actual) && (string?)actual == value);

        public void Dispose() => _listener.Dispose();
    }

    private sealed record Measurement<T>(string Instrument, T Value, IReadOnlyDictionary<string, object?> Tags)
    {
        public Measurement(string instrument, T value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
            : this(instrument, value, Copy(tags))
        {
        }

        private static Dictionary<string, object?> Copy(ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var copied = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var tag in tags)
            {
                copied[tag.Key] = tag.Value;
            }

            return copied;
        }
    }
}
