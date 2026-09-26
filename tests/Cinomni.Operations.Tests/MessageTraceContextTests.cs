using System.Diagnostics;
using Cinomni.Kernel.Diagnostics;
using Cinomni.Kernel.Messaging;
using Cinomni.Kernel.Results;
using Cinomni.Operations.Diagnostics;
using Cinomni.Operations.Messaging;
using Cinomni.Operations.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cinomni.Operations.Tests;

/// <summary>
/// The acceptance test for "one acquisition is one trace". Every cross-module flow in this product
/// crosses two asynchronous hops — the transactional outbox and the command queue — and a trace that
/// stops at either one is worth nothing to an operator asking why an acquisition stalled. So this
/// follows a single trace id all the way across both: request → event → relay → handler → command →
/// worker.
/// <para>
/// Its own database, because <c>OperationsTestHost</c> drops and recreates whatever it is given.
/// </para>
/// </summary>
public sealed class MessageTraceContextTests : IAsyncLifetime
{
    private readonly TraceSink _sink = new();
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync()
    {
        _provider = await OperationsTestHost.CreateAsync("cinomni_test_operations_trace", services =>
        {
            services.AddSingleton(_sink);
            services.AddIntegrationEvent<ThingHappened>("test.trace.thing-happened");
            services.AddScoped<IEventHandler<ThingHappened>, ThingHappenedHandler>();
            services.AddCommand<FollowUp>("test.trace.follow-up");
            services.AddScoped<ICommandHandler<FollowUp>, FollowUpHandler>();
        });
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task One_trace_spans_the_event_and_the_command_hop()
    {
        using var listener = ListenToCinomniSpans();

        // The originating unit of work — in production, an HTTP request or a scheduled tick.
        string rootTraceId;
        string rootSpanId;
        using (var root = CinomniTelemetry.Source.StartActivity("test.request", ActivityKind.Server))
        {
            Assert.NotNull(root);
            rootTraceId = root.TraceId.ToString();
            rootSpanId = root.SpanId.ToString();

            await using var scope = _provider.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IEventBus>().PublishAsync(new ThingHappened());
        }

        // Hop 1, written: the outbox row carries the publisher's context, in the same transaction.
        var message = await SingleAsync(db => db.Outbox);
        Assert.NotNull(message.TraceParent);
        Assert.Contains(rootTraceId, message.TraceParent, StringComparison.Ordinal);

        // Hop 1, read: the relay re-parents the dispatch, so the handler runs on the same trace under
        // a span of its own — not the publisher's span, and not a brand-new trace.
        await RelayAsync();
        Assert.Equal(rootTraceId, _sink.EventTraceId);
        Assert.NotEqual(rootSpanId, _sink.EventSpanId);

        // Hop 2, written: the command that handler enqueued inherits the same trace.
        var command = await SingleAsync(db => db.Commands);
        Assert.NotNull(command.TraceParent);
        Assert.Contains(rootTraceId, command.TraceParent, StringComparison.Ordinal);

        // Hop 2, read: and so does the worker that finally does the work.
        await ProcessCommandsAsync();
        Assert.Equal(rootTraceId, _sink.CommandTraceId);
        Assert.NotEqual(_sink.EventSpanId, _sink.CommandSpanId);
    }

    [Fact]
    public async Task Nothing_tracing_leaves_the_context_null_and_changes_no_behaviour()
    {
        // No listener and no ambient activity: the default build. Delivery must be untouched — trace
        // context is metadata, and a message that cannot be traced still has to be published.
        Assert.Null(Activity.Current);

        await using (var scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IEventBus>().PublishAsync(new ThingHappened());
        }

        var message = await SingleAsync(db => db.Outbox);
        Assert.Null(message.TraceParent);
        Assert.Null(message.TraceState);

        Assert.Equal(1, await RelayAsync());

        var published = await SingleAsync(db => db.Outbox);
        Assert.True(published.Published);

        var command = await SingleAsync(db => db.Commands);
        Assert.Null(command.TraceParent);
    }

    [Fact]
    public async Task A_redelivered_message_stays_on_the_trace_it_was_published_on()
    {
        using var listener = ListenToCinomniSpans();

        string rootTraceId;
        using (var root = CinomniTelemetry.Source.StartActivity("test.request", ActivityKind.Server))
        {
            Assert.NotNull(root);
            rootTraceId = root.TraceId.ToString();

            await using var scope = _provider.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IEventBus>().PublishAsync(new ThingHappened());
        }

        await RelayAsync();
        var firstDelivery = _sink.EventTraceId;

        // At-least-once means the relay can see the same row again after an interrupted commit. Put it
        // back and redeliver: the trace must be the original one, not the redelivering worker's.
        await using (var scope = _provider.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
            await dbContext.Database.ExecuteSqlAsync(
                $"UPDATE operations.outbox SET published = false, published_at = NULL");
        }

        _sink.Reset();
        await RelayAsync();

        Assert.Equal(rootTraceId, firstDelivery);
        Assert.Equal(rootTraceId, _sink.EventTraceId);
    }

    /// <summary>
    /// ASP.NET Core adopts the <c>tracestate</c> of any inbound request, so whatever a caller sends
    /// would otherwise be written to the outbox and the command queue and replayed on an exported span.
    /// Only a value that matches the W3C grammar is worth carrying; anything else is an arbitrary
    /// attacker-chosen string in this installation's own storage.
    /// </summary>
    [Theory]
    // Valid: a single member, several members with the optional whitespace, and a tenant@vendor key.
    [InlineData("vendor=value", true)]
    [InlineData("a=1, b=2", true)]
    [InlineData("tenant@vendor=opaque-value", true)]
    // Invalid: no key, uppercase and non-ASCII keys, a control character smuggled into a value, more
    // members than the grammar allows, and a value past the storage cap.
    [InlineData("=value", false)]
    [InlineData("VENDOR=value", false)]
    [InlineData("vendør=value", false)]
    [InlineData("vendor=one\ntwo", false)]
    [InlineData("vendor=one\ttwo", false)]
    [InlineData("a@b@c=value", false)]
    [InlineData("", false)]
    public void Only_a_tracestate_that_matches_the_grammar_is_carried(string traceState, bool expected) =>
        Assert.Equal(expected, MessageTracing.IsWellFormedTraceState(traceState));

    [Fact]
    public void A_tracestate_that_is_too_long_or_has_too_many_members_is_refused()
    {
        Assert.False(MessageTracing.IsWellFormedTraceState(
            "vendor=" + new string('x', MessageTracing.TraceStateLength)));

        Assert.False(MessageTracing.IsWellFormedTraceState(
            string.Join(',', Enumerable.Range(0, 40).Select(index => $"v{index}=x"))));
    }

    [Fact]
    public async Task A_hostile_tracestate_never_reaches_the_outbox()
    {
        using var listener = ListenToCinomniSpans();

        using (var root = CinomniTelemetry.Source.StartActivity("test.request", ActivityKind.Server))
        {
            Assert.NotNull(root);

            // What an inbound header can put on the ambient activity. The traceparent is generated by
            // the runtime and stays; the caller-supplied state must not.
            root.TraceStateString = "Injected Header=<script>";

            await using var scope = _provider.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IEventBus>().PublishAsync(new ThingHappened());
        }

        var message = await SingleAsync(db => db.Outbox);
        Assert.NotNull(message.TraceParent);
        Assert.Null(message.TraceState);
    }

    /// <summary>
    /// Samples everything on the one Cinomni source. Without a listener <c>StartActivity</c> returns
    /// null by design, which is exactly what makes an unconfigured installation pay nothing — and what
    /// makes a listener mandatory here.
    /// </summary>
    private static ActivityListener ListenToCinomniSpans()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == CinomniTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
        };

        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private async Task<int> RelayAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OutboxRelay>().ProcessBatchAsync();
    }

    private async Task<int> ProcessCommandsAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<CommandProcessor>().ProcessBatchAsync();
    }

    private async Task<T> SingleAsync<T>(Func<OperationsDbContext, DbSet<T>> set)
        where T : class
    {
        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
        return await set(dbContext).AsNoTracking().SingleAsync();
    }

    private sealed record ThingHappened : DomainEvent
    {
        public override string IdempotencyKey => "trace-test:thing";
    }

    private sealed record FollowUp : ICommand;

    private sealed class TraceSink
    {
        public string? EventTraceId { get; set; }

        public string? EventSpanId { get; set; }

        public string? CommandTraceId { get; set; }

        public string? CommandSpanId { get; set; }

        public void Reset()
        {
            EventTraceId = null;
            EventSpanId = null;
            CommandTraceId = null;
            CommandSpanId = null;
        }
    }

    /// <summary>Reacts to the event exactly as a real consumer does: by enqueuing its own command.</summary>
    private sealed class ThingHappenedHandler(TraceSink sink, ICommandQueue commandQueue)
        : IEventHandler<ThingHappened>
    {
        public async Task HandleAsync(ThingHappened domainEvent, CancellationToken cancellationToken = default)
        {
            sink.EventTraceId = Activity.Current?.TraceId.ToString();
            sink.EventSpanId = Activity.Current?.SpanId.ToString();

            await commandQueue.EnqueueAsync(new FollowUp(), "trace-test:follow-up", cancellationToken);
        }
    }

    private sealed class FollowUpHandler(TraceSink sink) : ICommandHandler<FollowUp>
    {
        public Task<Result> HandleAsync(FollowUp command, CancellationToken cancellationToken = default)
        {
            sink.CommandTraceId = Activity.Current?.TraceId.ToString();
            sink.CommandSpanId = Activity.Current?.SpanId.ToString();
            return Task.FromResult(Result.Success());
        }
    }
}
