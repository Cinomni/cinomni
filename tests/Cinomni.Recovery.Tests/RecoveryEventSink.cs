using System.Collections.Concurrent;
using Cinomni.Downloads.Contracts;
using Cinomni.Import.Contracts;
using Cinomni.Kernel.Messaging;

namespace Cinomni.Recovery.Tests;

/// <summary>
/// What the installation announced, for the whole run, across every restart in it.
/// <para>
/// This is where most of the recovery assertions actually land. A restart that re-imports a file
/// leaves the library looking correct — one asset, one path — and betrays itself only by announcing
/// the file twice, which is what every consumer downstream would then act on twice.
/// </para>
/// </summary>
internal sealed class RecoveryEventSink
{
    public ConcurrentBag<MediaAvailable> MediaAvailable { get; } = [];

    public ConcurrentBag<ImportCompleted> ImportCompleted { get; } = [];

    public ConcurrentBag<ImportFailed> ImportFailed { get; } = [];

    public ConcurrentBag<DownloadCompleted> DownloadCompleted { get; } = [];

    public ConcurrentBag<MetadataReady> MetadataReady { get; } = [];

    internal sealed class MediaAvailableRecorder(RecoveryEventSink sink) : IEventHandler<MediaAvailable>
    {
        public Task HandleAsync(MediaAvailable domainEvent, CancellationToken cancellationToken = default)
        {
            sink.MediaAvailable.Add(domainEvent);
            return Task.CompletedTask;
        }
    }

    internal sealed class ImportCompletedRecorder(RecoveryEventSink sink) : IEventHandler<ImportCompleted>
    {
        public Task HandleAsync(ImportCompleted domainEvent, CancellationToken cancellationToken = default)
        {
            sink.ImportCompleted.Add(domainEvent);
            return Task.CompletedTask;
        }
    }

    internal sealed class ImportFailedRecorder(RecoveryEventSink sink) : IEventHandler<ImportFailed>
    {
        public Task HandleAsync(ImportFailed domainEvent, CancellationToken cancellationToken = default)
        {
            sink.ImportFailed.Add(domainEvent);
            return Task.CompletedTask;
        }
    }

    internal sealed class DownloadCompletedRecorder(RecoveryEventSink sink) : IEventHandler<DownloadCompleted>
    {
        public Task HandleAsync(DownloadCompleted domainEvent, CancellationToken cancellationToken = default)
        {
            sink.DownloadCompleted.Add(domainEvent);
            return Task.CompletedTask;
        }
    }

    internal sealed class MetadataReadyRecorder(RecoveryEventSink sink) : IEventHandler<MetadataReady>
    {
        public Task HandleAsync(MetadataReady domainEvent, CancellationToken cancellationToken = default)
        {
            sink.MetadataReady.Add(domainEvent);
            return Task.CompletedTask;
        }
    }
}
