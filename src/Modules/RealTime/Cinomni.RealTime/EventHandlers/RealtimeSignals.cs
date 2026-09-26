using Cinomni.Acquisition.Contracts;
using Cinomni.Downloads.Contracts;
using Cinomni.Import.Contracts;
using Cinomni.Kernel.Messaging;
using Cinomni.Notifications.Contracts;
using Cinomni.RealTime.Streaming;
using Cinomni.Requests.Contracts;

namespace Cinomni.RealTime.EventHandlers;

/// <summary>
/// Turns the integration events worth watching into live signals. One handler class per producing
/// module, each implementing <see cref="IEventHandler{T}"/> once per event it reacts to: the reaction
/// is the same line every time — say the topic changed — and a class per event would be twenty files
/// saying it.
/// <para>
/// These run inside the outbox relay's transaction, so they must do nothing slow and nothing that can
/// fail: <see cref="RealtimeHub.Publish"/> is a non-blocking in-memory fan-out, and a signal lost to a
/// full buffer costs nothing because the client re-reads REST for the real answer.
/// </para>
/// </summary>
internal sealed class DownloadSignals(RealtimeHub hub)
    : IEventHandler<DownloadStarted>,
        IEventHandler<MetadataReady>,
        IEventHandler<DownloadCompleted>,
        IEventHandler<DownloadFailed>,
        IEventHandler<TunnelEgressLost>,
        IEventHandler<TunnelEgressRestored>
{
    public Task HandleAsync(DownloadStarted domainEvent, CancellationToken cancellationToken = default) =>
        Signal();

    // Egress transitions move every in-flight download at once, so the same "downloads changed"
    // signal is the right one: the client re-reads the list it already knows how to read, and no new
    // topic — which would need a matching client change to mean anything — is invented for it.
    public Task HandleAsync(TunnelEgressLost domainEvent, CancellationToken cancellationToken = default) =>
        Signal();

    public Task HandleAsync(TunnelEgressRestored domainEvent, CancellationToken cancellationToken = default) =>
        Signal();

    public Task HandleAsync(MetadataReady domainEvent, CancellationToken cancellationToken = default) =>
        Signal();

    public Task HandleAsync(DownloadCompleted domainEvent, CancellationToken cancellationToken = default) =>
        Signal();

    public Task HandleAsync(DownloadFailed domainEvent, CancellationToken cancellationToken = default) =>
        Signal();

    private Task Signal()
    {
        hub.Publish(new RealtimeMessage(RealtimeMessage.Topics.Downloads, RealtimeAudience.Operators));
        return Task.CompletedTask;
    }
}

/// <summary>Acquisition goals moving — what the activity view lists.</summary>
internal sealed class AcquisitionSignals(RealtimeHub hub)
    : IEventHandler<AcquisitionRequested>,
        IEventHandler<CandidateSelected>,
        IEventHandler<DownloadQueued>,
        IEventHandler<AcquisitionSucceeded>,
        IEventHandler<AcquisitionFailed>,
        IEventHandler<AcquisitionRetrying>
{
    public Task HandleAsync(AcquisitionRequested domainEvent, CancellationToken cancellationToken = default) =>
        Signal();

    public Task HandleAsync(CandidateSelected domainEvent, CancellationToken cancellationToken = default) =>
        Signal();

    public Task HandleAsync(DownloadQueued domainEvent, CancellationToken cancellationToken = default) =>
        Signal();

    public Task HandleAsync(AcquisitionSucceeded domainEvent, CancellationToken cancellationToken = default) =>
        Signal();

    public Task HandleAsync(AcquisitionFailed domainEvent, CancellationToken cancellationToken = default) =>
        Signal();

    public Task HandleAsync(AcquisitionRetrying domainEvent, CancellationToken cancellationToken = default) =>
        Signal();

    private Task Signal()
    {
        hub.Publish(new RealtimeMessage(RealtimeMessage.Topics.Activity, RealtimeAudience.Operators));
        return Task.CompletedTask;
    }
}

/// <summary>
/// Content landed. Everyone gets this one: the library read model narrows by collection on every read,
/// and the signal itself names nothing — a viewer who may see none of it simply re-reads the same list.
/// </summary>
internal sealed class LibrarySignals(RealtimeHub hub) : IEventHandler<MediaAvailable>
{
    public Task HandleAsync(MediaAvailable domainEvent, CancellationToken cancellationToken = default)
    {
        hub.Publish(new RealtimeMessage(RealtimeMessage.Topics.Library, RealtimeAudience.Everyone));
        return Task.CompletedTask;
    }
}

/// <summary>
/// An inbox changed. The audience mirrors the notification's own: an operator concern never reaches a
/// household member's connection, exactly as it never reaches their inbox.
/// </summary>
internal sealed class NotificationSignals(RealtimeHub hub) : IEventHandler<NotificationRaised>
{
    public Task HandleAsync(NotificationRaised domainEvent, CancellationToken cancellationToken = default)
    {
        hub.Publish(new RealtimeMessage(
            RealtimeMessage.Topics.Notifications,
            domainEvent.AdminOnly ? RealtimeAudience.Operators : RealtimeAudience.Everyone));
        return Task.CompletedTask;
    }
}

/// <summary>
/// Requests. A submission is an operator concern (it is their approval queue); a decision on one is
/// addressed to the person who asked, and to the operators whose queue just shrank.
/// </summary>
internal sealed class RequestSignals(RealtimeHub hub)
    : IEventHandler<MediaRequested>,
        IEventHandler<MediaRequestApproved>,
        IEventHandler<MediaRequestRejected>
{
    public Task HandleAsync(MediaRequested domainEvent, CancellationToken cancellationToken = default)
    {
        Signal(RealtimeAudience.Operators);
        Signal(RealtimeAudience.Account(domainEvent.RequestedByUserId));
        return Task.CompletedTask;
    }

    public Task HandleAsync(MediaRequestApproved domainEvent, CancellationToken cancellationToken = default)
    {
        Signal(RealtimeAudience.Operators);
        Signal(RealtimeAudience.Account(domainEvent.RequestedByUserId));
        return Task.CompletedTask;
    }

    public Task HandleAsync(MediaRequestRejected domainEvent, CancellationToken cancellationToken = default)
    {
        Signal(RealtimeAudience.Operators);
        Signal(RealtimeAudience.Account(domainEvent.RequestedByUserId));
        return Task.CompletedTask;
    }

    private void Signal(RealtimeAudience audience) =>
        hub.Publish(new RealtimeMessage(RealtimeMessage.Topics.Requests, audience));
}
