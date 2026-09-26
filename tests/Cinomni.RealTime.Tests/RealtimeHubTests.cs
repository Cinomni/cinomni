using Cinomni.Kernel.Security;
using Cinomni.RealTime.Streaming;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cinomni.RealTime.Tests;

/// <summary>
/// The fan-out is the trust boundary of the live stream: a connection must receive exactly what its
/// account is entitled to, and the hub must never let a slow or absent consumer affect the publisher —
/// it is called from inside the outbox relay's transaction.
/// </summary>
public sealed class RealtimeHubTests
{
    private static readonly Viewer Administrator = new(Guid.NewGuid(), IsAdministrator: true);
    private static readonly Viewer Member = new(Guid.NewGuid(), IsAdministrator: false);

    private static RealtimeHub NewHub() => new(NullLogger<RealtimeHub>.Instance);

    [Fact]
    public void One_account_cannot_take_every_slot_from_the_household()
    {
        // Arrange — one account opens as many streams as it is allowed.
        var hub = NewHub();
        var greedy = new Viewer(Guid.NewGuid(), IsAdministrator: false);
        var open = Enumerable.Range(0, RealtimeHub.MaxConnectionsPerAccount).Select(_ => hub.Connect(greedy)).ToList();

        // Act
        var oneMore = hub.Connect(greedy);
        using var someoneElse = hub.Connect(Member);

        // Assert — refused for that account only, and a slot frees when one of its streams closes.
        Assert.All(open, Assert.NotNull);
        Assert.Null(oneMore);
        Assert.NotNull(someoneElse);
        open[0]!.Dispose();
        using var again = hub.Connect(greedy);
        Assert.NotNull(again);
        foreach (var connection in open.Skip(1))
        {
            connection!.Dispose();
        }
    }

    [Fact]
    public void An_operator_signal_never_reaches_a_member()
    {
        var hub = NewHub();
        using var member = hub.Connect(Member);
        Assert.NotNull(member);

        hub.Publish(new RealtimeMessage(RealtimeMessage.Topics.Downloads, RealtimeAudience.Operators));

        Assert.False(member.Reader.TryRead(out _));
    }

    [Fact]
    public void An_operator_signal_reaches_an_administrator()
    {
        var hub = NewHub();
        using var admin = hub.Connect(Administrator);
        Assert.NotNull(admin);

        hub.Publish(new RealtimeMessage(RealtimeMessage.Topics.Downloads, RealtimeAudience.Operators));

        Assert.True(admin.Reader.TryRead(out var message));
        Assert.Equal(RealtimeMessage.Topics.Downloads, message.Topic);
    }

    [Fact]
    public void An_everyone_signal_reaches_every_connection()
    {
        var hub = NewHub();
        using var admin = hub.Connect(Administrator);
        using var member = hub.Connect(Member);
        Assert.NotNull(admin);
        Assert.NotNull(member);

        hub.Publish(new RealtimeMessage(RealtimeMessage.Topics.Library, RealtimeAudience.Everyone));

        Assert.True(admin.Reader.TryRead(out _));
        Assert.True(member.Reader.TryRead(out _));
    }

    [Fact]
    public void An_account_signal_reaches_only_that_account()
    {
        var hub = NewHub();
        using var mine = hub.Connect(Member);
        using var other = hub.Connect(Administrator);
        Assert.NotNull(mine);
        Assert.NotNull(other);

        hub.Publish(new RealtimeMessage(
            RealtimeMessage.Topics.Requests, RealtimeAudience.Account(Member.UserId)));

        Assert.True(mine.Reader.TryRead(out _));
        // Addressed to one account: being an administrator does not make it yours.
        Assert.False(other.Reader.TryRead(out _));
    }

    [Fact]
    public void A_disposed_connection_stops_receiving_and_frees_its_slot()
    {
        var hub = NewHub();
        var connection = hub.Connect(Administrator);
        Assert.NotNull(connection);
        Assert.Equal(1, hub.ConnectionCount);

        connection.Dispose();
        hub.Publish(new RealtimeMessage(RealtimeMessage.Topics.Downloads, RealtimeAudience.Operators));

        Assert.Equal(0, hub.ConnectionCount);
        Assert.False(connection.Reader.TryRead(out _));
    }

    [Fact]
    public void Publishing_to_a_full_buffer_drops_the_oldest_rather_than_blocking()
    {
        var hub = NewHub();
        using var admin = hub.Connect(Administrator);
        Assert.NotNull(admin);

        // Far past the per-connection buffer: the publisher must return regardless, because it is the
        // outbox relay's thread and a blocked write there would stall every event in the system.
        for (var i = 0; i < 500; i++)
        {
            hub.Publish(new RealtimeMessage(RealtimeMessage.Topics.Activity, RealtimeAudience.Operators));
        }

        var drained = 0;
        while (admin.Reader.TryRead(out _))
        {
            drained++;
        }

        Assert.True(drained > 0);
        Assert.True(drained <= 500);
    }

    [Fact]
    public void The_hub_refuses_a_connection_past_its_ceiling()
    {
        var hub = NewHub();
        var open = new List<RealtimeConnection>();
        try
        {
            // A different account each time: one account alone stops at its own, lower ceiling.
            for (var i = 0; i < RealtimeHub.MaxConnections; i++)
            {
                var connection = hub.Connect(new Viewer(Guid.NewGuid(), IsAdministrator: false));
                Assert.NotNull(connection);
                open.Add(connection);
            }

            Assert.Null(hub.Connect(Administrator));
        }
        finally
        {
            foreach (var connection in open)
            {
                connection.Dispose();
            }
        }
    }

    [Fact]
    public void HasOperators_is_false_while_only_members_are_connected()
    {
        var hub = NewHub();
        using var member = hub.Connect(Member);
        Assert.NotNull(member);

        // This is what keeps the progress publisher off the database when nobody can see its output.
        Assert.False(hub.HasOperators);
    }
}
