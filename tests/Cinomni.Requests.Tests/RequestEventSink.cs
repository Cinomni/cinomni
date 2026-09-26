using System.Collections.Concurrent;
using Cinomni.Kernel.Messaging;
using Cinomni.Requests.Contracts;

namespace Cinomni.Requests.Tests;

/// <summary>
/// Captures the integration events the module publishes, so tests can assert what the rest of the platform
/// actually receives — not just what the module stored. Registered as a singleton with one handler per event.
/// </summary>
internal sealed class RequestEventSink
{
    public ConcurrentBag<MediaRequested> Requested { get; } = [];

    public ConcurrentBag<MediaRequestApproved> Approved { get; } = [];

    public ConcurrentBag<MediaRequestRejected> Rejected { get; } = [];
}

internal sealed class RequestedSink(RequestEventSink sink) : IEventHandler<MediaRequested>
{
    public Task HandleAsync(MediaRequested domainEvent, CancellationToken cancellationToken = default)
    {
        sink.Requested.Add(domainEvent);
        return Task.CompletedTask;
    }
}

internal sealed class ApprovedSink(RequestEventSink sink) : IEventHandler<MediaRequestApproved>
{
    public Task HandleAsync(MediaRequestApproved domainEvent, CancellationToken cancellationToken = default)
    {
        sink.Approved.Add(domainEvent);
        return Task.CompletedTask;
    }
}

internal sealed class RejectedSink(RequestEventSink sink) : IEventHandler<MediaRequestRejected>
{
    public Task HandleAsync(MediaRequestRejected domainEvent, CancellationToken cancellationToken = default)
    {
        sink.Rejected.Add(domainEvent);
        return Task.CompletedTask;
    }
}
