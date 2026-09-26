using System.Collections.Concurrent;
using Cinomni.Kernel.Messaging;
using Cinomni.Operations.Messaging;
using Cinomni.Operations.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cinomni.Operations.Tests;

/// <summary>
/// Integration test for the transactional outbox against a real PostgreSQL instance.
/// Verifies the round trip: publish enqueues, the relay claims and dispatches, the handler
/// receives the event, and the message is marked published.
/// </summary>
public sealed class OutboxRelayTests : IAsyncLifetime
{
    private readonly CaptureSink _sink = new();
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync()
    {
        _provider = await OperationsTestHost.CreateAsync("cinomni_test_outbox", services =>
        {
            services.AddIntegrationEvent<ThingHappened>("test.thing-happened");
            services.AddIntegrationEvent<ThingEchoed>("test.thing-echoed");
            services.AddSingleton(_sink);
            services.AddScoped<IEventHandler<ThingHappened>, ThingHappenedHandler>();
        });
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task Published_event_is_delivered_to_its_handler_and_marked_published()
    {
        var thingId = Guid.NewGuid();

        await using (var scope = _provider.CreateAsyncScope())
        {
            var eventBus = scope.ServiceProvider.GetRequiredService<IEventBus>();
            await eventBus.PublishAsync(new ThingHappened(thingId));
        }

        int published;
        await using (var scope = _provider.CreateAsyncScope())
        {
            var relay = scope.ServiceProvider.GetRequiredService<OutboxRelay>();
            published = await relay.ProcessBatchAsync();
        }

        Assert.Equal(1, published);
        Assert.Contains(thingId, _sink.Received);

        await using (var scope = _provider.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
            var message = await dbContext.Outbox.SingleAsync();
            Assert.True(message.Published);
            Assert.NotNull(message.PublishedAt);
        }

        await using (var scope = _provider.CreateAsyncScope())
        {
            var relay = scope.ServiceProvider.GetRequiredService<OutboxRelay>();
            Assert.Equal(0, await relay.ProcessBatchAsync());
        }
    }

    [Fact]
    public async Task A_message_whose_handler_fails_does_not_hold_back_the_ones_behind_it()
    {
        // Arrange — the oldest message's handler always throws, after publishing a follow-up event.
        var poison = Guid.NewGuid();
        var healthy = Guid.NewGuid();
        _sink.Poison.Add(poison);
        await PublishAsync(new ThingHappened(poison));
        await PublishAsync(new ThingHappened(healthy));

        // Act
        var published = await RelayOnceAsync();

        // Assert — the one behind it was delivered; the poison one was rolled back alone, is waiting
        // out a backoff, and what its handler wrote before throwing did not survive.
        Assert.Equal(1, published);
        Assert.Contains(healthy, _sink.Received);
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
        var failed = await db.Outbox.SingleAsync(m => m.IdempotencyKey == $"thing:{poison}");
        Assert.False(failed.Published);
        Assert.Equal(1, failed.Attempts);
        Assert.NotNull(failed.NextAttemptAt);
        Assert.Equal(nameof(InvalidOperationException), failed.LastError);
        Assert.False(await db.Outbox.AnyAsync(m => m.IdempotencyKey == $"echo:{poison}"));
        Assert.Equal(0, await RelayOnceAsync());
    }

    [Fact]
    public async Task A_handlers_own_timeout_is_a_failed_delivery_not_a_blocked_queue()
    {
        // A timeout inside a handler is a TaskCanceledException, which is also what a host shutdown
        // looks like; only the shutdown may escape the per-message isolation.
        var timesOut = Guid.NewGuid();
        var healthy = Guid.NewGuid();
        _sink.TimesOut.Add(timesOut);
        await PublishAsync(new ThingHappened(timesOut));
        await PublishAsync(new ThingHappened(healthy));

        Assert.Equal(1, await RelayOnceAsync());

        Assert.Contains(healthy, _sink.Received);
        await using var scope = _provider.CreateAsyncScope();
        var failed = await scope.ServiceProvider.GetRequiredService<OperationsDbContext>().Outbox
            .SingleAsync(m => m.IdempotencyKey == $"thing:{timesOut}");
        Assert.Equal(1, failed.Attempts);
        Assert.Equal(nameof(TaskCanceledException), failed.LastError);
    }

    [Fact]
    public async Task A_message_that_keeps_failing_is_set_aside_and_no_longer_offered()
    {
        var poison = Guid.NewGuid();
        _sink.Poison.Add(poison);
        await PublishAsync(new ThingHappened(poison));

        for (var attempt = 0; attempt < OutboxRelay.MaxAttempts; attempt++)
        {
            await RelayOnceAsync();
            await ExpireBackoffAsync();
        }

        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
        var dead = await db.Outbox.SingleAsync(m => m.IdempotencyKey == $"thing:{poison}");
        Assert.NotNull(dead.DeadLetteredAt);
        Assert.Equal(OutboxRelay.MaxAttempts, dead.Attempts);
        Assert.False(dead.Published);
        Assert.Equal(0, await RelayOnceAsync());
    }

    private async Task PublishAsync(IDomainEvent domainEvent)
    {
        await using var scope = _provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IEventBus>().PublishAsync(domainEvent);
    }

    private async Task<int> RelayOnceAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OutboxRelay>().ProcessBatchAsync();
    }

    private async Task ExpireBackoffAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<OperationsDbContext>().Database.ExecuteSqlRawAsync(
            "UPDATE operations.outbox SET next_attempt_at = NULL");
    }

    private sealed record ThingHappened(Guid ThingId) : DomainEvent
    {
        public override string IdempotencyKey => $"thing:{ThingId}";
    }

    private sealed record ThingEchoed(Guid ThingId) : DomainEvent
    {
        public override string IdempotencyKey => $"echo:{ThingId}";
    }

    private sealed class CaptureSink
    {
        public ConcurrentBag<Guid> Received { get; } = [];

        /// <summary>Things whose handler always fails, after it has published a follow-up event.</summary>
        public ConcurrentBag<Guid> Poison { get; } = [];

        /// <summary>Things whose handler times out on its own.</summary>
        public ConcurrentBag<Guid> TimesOut { get; } = [];
    }

    private sealed class ThingHappenedHandler(CaptureSink sink, IEventBus eventBus) : IEventHandler<ThingHappened>
    {
        public async Task HandleAsync(ThingHappened domainEvent, CancellationToken cancellationToken = default)
        {
            if (sink.Poison.Contains(domainEvent.ThingId))
            {
                await eventBus.PublishAsync(new ThingEchoed(domainEvent.ThingId), cancellationToken);
                throw new InvalidOperationException("This handler cannot process it.");
            }

            if (sink.TimesOut.Contains(domainEvent.ThingId))
            {
                throw new TaskCanceledException("The handler's own call timed out.");
            }

            sink.Received.Add(domainEvent.ThingId);
        }
    }
}
