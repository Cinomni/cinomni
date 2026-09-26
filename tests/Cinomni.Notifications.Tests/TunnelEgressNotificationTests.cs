using Cinomni.Downloads.Contracts;
using Cinomni.Kernel.Messaging;
using Cinomni.Kernel.Security;
using Cinomni.Notifications.Contracts;
using Cinomni.Operations.Messaging;
using Cinomni.Operations.Transactions;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Notifications.Tests;

/// <summary>
/// What an operator is actually told when torrent egress stops being verifiable, driven through the
/// real handlers in the module that owns them.
/// <para>
/// The duplicate-delivery assertion is the point. Delivery is at-least-once, so one outage genuinely
/// reaches these handlers several times, and the only thing standing between that and a notification
/// per redelivered outbox row is the transition sequence in the idempotency key. A change to that key
/// has to fail here rather than in production during an outage.
/// </para>
/// </summary>
public sealed class TunnelEgressNotificationTests : IAsyncLifetime
{
    private const string LeakReason = "egress-identity-not-the-tunnel";

    private static readonly Viewer Operator = new(Guid.NewGuid(), IsAdministrator: true);
    private static readonly Viewer Member = new(Guid.NewGuid(), IsAdministrator: false);

    private readonly FakeNotificationDispatcher _dispatcher = new();
    private ServiceProvider _host = null!;

    public async Task InitializeAsync() =>
        _host = await NotificationsTestHost.CreateAsync("cinomni_test_notifications_tunnel", _dispatcher);

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task A_lost_egress_tells_the_administrator_what_was_observed()
    {
        await PublishAsync(new TunnelEgressLost(1, LeakReason, 3, nameof(TunnelLossPolicy.Block)));
        await DrainAsync();

        var notification = Assert.Single(await ListAsync(Operator));
        Assert.Equal("tunnel-egress-lost", notification.Type);
        Assert.Equal(NotificationSeverity.Error, notification.Severity);
        // The observed cause is carried through verbatim: it is a vocabulary, and guessing at it
        // sends an operator to the wrong subsystem.
        Assert.Contains(LeakReason, notification.Body, StringComparison.Ordinal);

        // Nobody else can act on this, and it names what the installation is doing on its own.
        Assert.Empty(await ListAsync(Member));
    }

    [Fact]
    public async Task Redelivering_one_outage_notifies_once()
    {
        var outage = new TunnelEgressLost(1, LeakReason, 3, nameof(TunnelLossPolicy.Block));

        await PublishAsync(outage);
        await DrainAsync();
        await PublishAsync(outage);
        await DrainAsync();

        Assert.Single(await ListAsync(Operator));
    }

    [Fact]
    public async Task A_second_outage_is_its_own_notification()
    {
        // The other half of the same rule: a key that named only "the tunnel broke" would swallow
        // every outage after the first, and an installation that flaps would report one incident ever.
        await PublishAsync(new TunnelEgressLost(1, LeakReason, 1, nameof(TunnelLossPolicy.Block)));
        await PublishAsync(new TunnelEgressLost(2, "tunnel-device-missing", 2, nameof(TunnelLossPolicy.Block)));
        await DrainAsync();

        Assert.Equal(2, (await ListAsync(Operator)).Count);
    }

    [Fact]
    public async Task A_restored_egress_says_the_holds_were_lifted_and_deduplicates_too()
    {
        var restored = new TunnelEgressRestored(1, 3);

        await PublishAsync(restored);
        await DrainAsync();
        await PublishAsync(restored);
        await DrainAsync();

        var notification = Assert.Single(await ListAsync(Operator));
        Assert.Equal("tunnel-egress-restored", notification.Type);
        Assert.Equal(NotificationSeverity.Success, notification.Severity);
    }

    [Fact]
    public async Task The_loss_and_its_restoration_are_two_notifications_of_one_incident()
    {
        await PublishAsync(new TunnelEgressLost(1, LeakReason, 1, nameof(TunnelLossPolicy.Block)));
        await PublishAsync(new TunnelEgressRestored(1, 1));
        await DrainAsync();

        var notifications = await ListAsync(Operator);
        Assert.Equal(2, notifications.Count);
        Assert.Contains(notifications, n => n.Type == "tunnel-egress-lost");
        Assert.Contains(notifications, n => n.Type == "tunnel-egress-restored");
    }

    // -- helpers ---------------------------------------------------------------------------------

    private async Task PublishAsync(IDomainEvent domainEvent)
    {
        await using var scope = _host.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var eventBus = scope.ServiceProvider.GetRequiredService<IEventBus>();
        await unitOfWork.ExecuteAsync(async token => await eventBus.PublishAsync(domainEvent, token));
    }

    private async Task<IReadOnlyList<Notification>> ListAsync(Viewer reader)
    {
        await using var scope = _host.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INotificationQuery>()
            .ListAsync(reader, unreadOnly: false, 50);
    }

    private async Task DrainAsync()
    {
        while (true)
        {
            var commands = await DrainCommandsAsync();
            var events = await DrainOutboxAsync();
            if (commands == 0 && events == 0)
            {
                break;
            }
        }
    }

    private async Task<int> DrainCommandsAsync()
    {
        await using var scope = _host.CreateAsyncScope();
        var processor = scope.ServiceProvider.GetRequiredService<CommandProcessor>();
        var total = 0;
        int processed;
        while ((processed = await processor.ProcessBatchAsync()) > 0)
        {
            total += processed;
        }

        return total;
    }

    private async Task<int> DrainOutboxAsync()
    {
        await using var scope = _host.CreateAsyncScope();
        var relay = scope.ServiceProvider.GetRequiredService<OutboxRelay>();
        var total = 0;
        int published;
        while ((published = await relay.ProcessBatchAsync()) > 0)
        {
            total += published;
        }

        return total;
    }
}
