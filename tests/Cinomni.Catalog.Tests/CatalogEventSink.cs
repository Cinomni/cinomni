using Cinomni.Catalog.Contracts;
using Cinomni.Kernel.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Catalog.Tests;

/// <summary>
/// Collects the Catalog integration events the relay delivers, so a flow test can assert on <i>what was
/// announced</i> rather than only on the rows that were written. One sink per event type, all writing into
/// the same instance behind a lock — the relay dispatches on a background-shaped path and the drain loop
/// runs several scopes.
/// </summary>
internal sealed class CatalogEventSink
{
    private readonly Lock _gate = new();

    public List<WorkAdded> WorkAdded { get; } = [];

    public List<WorkAvailable> WorkAvailable { get; } = [];

    public List<EpisodeAvailable> EpisodeAvailable { get; } = [];

    public List<SeriesStructureChanged> StructureChanged { get; } = [];

    /// <summary>Registers the sink and a handler for every Catalog event it collects.</summary>
    public void Register(IServiceCollection services)
    {
        services.AddSingleton(this);
        services.AddScoped<IEventHandler<WorkAdded>, WorkAddedSink>();
        services.AddScoped<IEventHandler<WorkAvailable>, WorkAvailableSink>();
        services.AddScoped<IEventHandler<EpisodeAvailable>, EpisodeAvailableSink>();
        services.AddScoped<IEventHandler<SeriesStructureChanged>, StructureChangedSink>();
    }

    private void Add<T>(List<T> target, T item)
    {
        lock (_gate)
        {
            target.Add(item);
        }
    }

    private sealed class WorkAddedSink(CatalogEventSink sink) : IEventHandler<WorkAdded>
    {
        public Task HandleAsync(WorkAdded domainEvent, CancellationToken cancellationToken = default)
        {
            sink.Add(sink.WorkAdded, domainEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class WorkAvailableSink(CatalogEventSink sink) : IEventHandler<WorkAvailable>
    {
        public Task HandleAsync(WorkAvailable domainEvent, CancellationToken cancellationToken = default)
        {
            sink.Add(sink.WorkAvailable, domainEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class EpisodeAvailableSink(CatalogEventSink sink) : IEventHandler<EpisodeAvailable>
    {
        public Task HandleAsync(EpisodeAvailable domainEvent, CancellationToken cancellationToken = default)
        {
            sink.Add(sink.EpisodeAvailable, domainEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class StructureChangedSink(CatalogEventSink sink) : IEventHandler<SeriesStructureChanged>
    {
        public Task HandleAsync(SeriesStructureChanged domainEvent, CancellationToken cancellationToken = default)
        {
            sink.Add(sink.StructureChanged, domainEvent);
            return Task.CompletedTask;
        }
    }
}
