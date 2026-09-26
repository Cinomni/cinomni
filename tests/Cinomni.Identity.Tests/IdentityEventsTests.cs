using System.Collections.Concurrent;
using Cinomni.Identity.Application;
using Cinomni.Identity.Events;
using Cinomni.Identity.Persistence;
using Cinomni.Kernel.Messaging;
using Cinomni.Operations.Messaging;
using Cinomni.Operations.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cinomni.Identity.Tests;

/// <summary>
/// Proves the atomic outbox: creating a user writes the account row and its
/// UserCreated event in one transaction, and the relay then delivers the event.
/// </summary>
public sealed class IdentityEventsTests : IAsyncLifetime
{
    private readonly EventSink _sink = new();
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync() =>
        _provider = await IdentityTestHost.CreateAsync("cinomni_test_identity_events", services =>
        {
            services.AddSingleton(_sink);
            services.AddScoped<IEventHandler<UserCreated>, UserCreatedHandler>();
        });

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task CreateAdmin_writes_user_and_event_atomically_then_relay_delivers()
    {
        Guid userId;
        await using (var scope = _provider.CreateAsyncScope())
        {
            var provisioning = scope.ServiceProvider.GetRequiredService<IUserProvisioning>();
            var created = await provisioning.CreateAdminAsync("admin", "correct-horse");
            Assert.True(created.IsSuccess);
            userId = created.Value.Value;
        }

        // Same commit: the account row and the outbox event are both present.
        await using (var scope = _provider.CreateAsyncScope())
        {
            var identityDb = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            var operationsDb = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();

            Assert.Equal(1, await identityDb.Users.CountAsync());

            var message = await operationsDb.Outbox.SingleAsync();
            Assert.Equal(IdentityEventNames.UserCreated, message.EventType);
            Assert.False(message.Published);
        }

        // The relay delivers UserCreated to its handler.
        await using (var scope = _provider.CreateAsyncScope())
        {
            var relay = scope.ServiceProvider.GetRequiredService<OutboxRelay>();
            Assert.Equal(1, await relay.ProcessBatchAsync());
        }

        Assert.Contains(userId, _sink.Users);
    }

    private sealed class EventSink
    {
        public ConcurrentBag<Guid> Users { get; } = [];
    }

    private sealed class UserCreatedHandler(EventSink sink) : IEventHandler<UserCreated>
    {
        public Task HandleAsync(UserCreated domainEvent, CancellationToken cancellationToken = default)
        {
            sink.Users.Add(domainEvent.UserId);
            return Task.CompletedTask;
        }
    }
}
