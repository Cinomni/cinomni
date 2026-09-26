using Cinomni.Downloads.Contracts;
using Cinomni.Kernel.Security;
using Cinomni.Notifications.Contracts;
using Cinomni.RealTime.EventHandlers;
using Cinomni.RealTime.Streaming;
using Cinomni.Requests.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cinomni.RealTime.Tests;

/// <summary>
/// What each integration event turns into on the wire. The audience is the whole security story of the
/// stream: get it wrong here and a household member learns about an operator's downloads, or an
/// administrator-only notification goes to everyone.
/// </summary>
public sealed class RealtimeSignalTests
{
    private static readonly Viewer Administrator = new(Guid.NewGuid(), IsAdministrator: true);
    private static readonly Viewer Member = new(Guid.NewGuid(), IsAdministrator: false);

    [Fact]
    public async Task A_download_event_signals_operators_only()
    {
        var hub = new RealtimeHub(NullLogger<RealtimeHub>.Instance);
        using var admin = hub.Connect(Administrator);
        using var member = hub.Connect(Member);
        Assert.NotNull(admin);
        Assert.NotNull(member);

        await new DownloadSignals(hub).HandleAsync(
            new DownloadStarted(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "hash"));

        Assert.True(admin.Reader.TryRead(out var message));
        Assert.Equal(RealtimeMessage.Topics.Downloads, message.Topic);
        Assert.Null(message.Payload);
        Assert.False(member.Reader.TryRead(out _));
    }

    [Fact]
    public async Task An_egress_transition_signals_the_downloads_topic_to_operators_only()
    {
        // A loss and its restoration move every in-flight download at once, so the client re-reads the
        // list it already knows how to read. What must not happen is a household member learning that
        // the operator's downloads stopped, or the transition inventing a topic no client listens to.
        var hub = new RealtimeHub(NullLogger<RealtimeHub>.Instance);
        using var admin = hub.Connect(Administrator);
        using var member = hub.Connect(Member);
        Assert.NotNull(admin);
        Assert.NotNull(member);
        var signals = new DownloadSignals(hub);

        await signals.HandleAsync(new TunnelEgressLost(1, "egress-identity-not-the-tunnel", 2, "Block"));
        await signals.HandleAsync(new TunnelEgressRestored(1, 2));

        Assert.True(admin.Reader.TryRead(out var lost));
        Assert.Equal(RealtimeMessage.Topics.Downloads, lost.Topic);
        Assert.True(admin.Reader.TryRead(out var restored));
        Assert.Equal(RealtimeMessage.Topics.Downloads, restored.Topic);
        Assert.False(member.Reader.TryRead(out _));
    }

    [Fact]
    public async Task An_admin_only_notification_never_reaches_a_member()
    {
        var hub = new RealtimeHub(NullLogger<RealtimeHub>.Instance);
        using var member = hub.Connect(Member);
        Assert.NotNull(member);

        await new NotificationSignals(hub).HandleAsync(
            new NotificationRaised(Guid.NewGuid(), "acquisition.failed", "Error", AdminOnly: true, WorkId: null));

        Assert.False(member.Reader.TryRead(out _));
    }

    [Fact]
    public async Task A_household_notification_reaches_a_member()
    {
        var hub = new RealtimeHub(NullLogger<RealtimeHub>.Instance);
        using var member = hub.Connect(Member);
        Assert.NotNull(member);

        await new NotificationSignals(hub).HandleAsync(
            new NotificationRaised(Guid.NewGuid(), "media.available", "Success", AdminOnly: false, WorkId: Guid.NewGuid()));

        Assert.True(member.Reader.TryRead(out var message));
        Assert.Equal(RealtimeMessage.Topics.Notifications, message.Topic);
        // A signal, not a record: the work id stays out of it and the client re-reads its own inbox.
        Assert.Null(message.Payload);
    }

    [Fact]
    public async Task A_resolved_request_reaches_its_requester_and_the_operators()
    {
        var hub = new RealtimeHub(NullLogger<RealtimeHub>.Instance);
        using var admin = hub.Connect(Administrator);
        using var requester = hub.Connect(Member);
        var bystander = new Viewer(Guid.NewGuid(), IsAdministrator: false);
        using var other = hub.Connect(bystander);
        Assert.NotNull(admin);
        Assert.NotNull(requester);
        Assert.NotNull(other);

        await new RequestSignals(hub).HandleAsync(
            new MediaRequestRejected(Guid.NewGuid(), "Solaris", Member.UserId, "Not this one"));

        Assert.True(admin.Reader.TryRead(out _));
        Assert.True(requester.Reader.TryRead(out _));
        // Somebody else's request is none of this account's business.
        Assert.False(other.Reader.TryRead(out _));
    }
}
