namespace Cinomni.Discovery.Persistence;

/// <summary>Durable UTC-day accounting for external indexer requests.</summary>
public sealed class IndexerUsage
{
    public Guid IndexerId { get; init; }

    public DateOnly UsageDate { get; init; }

    public int QueryCount { get; set; }

    public int GrabCount { get; set; }
}
