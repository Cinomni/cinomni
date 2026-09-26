using Cinomni.Kernel.Messaging;

namespace Cinomni.Discovery.Contracts;

/// <summary>Stable registered names of the Discovery integration events.</summary>
public static class DiscoveryEventNames
{
    public const string SearchCompleted = "discovery.search-completed";
}

/// <summary>
/// A federated search finished. Best-effort by design (event-catalog: a lost one is simply
/// re-driven by the next SearchRequested); it carries the execution id and count, not the
/// results — a consumer pulls those through <see cref="IReleaseSearchResults"/>. The optional
/// <paramref name="TargetId"/> correlates back to the monitored target that triggered it.
/// </summary>
public sealed record SearchCompleted(Guid SearchId, Guid? TargetId, int ResultCount) : DomainEvent
{
    public override string IdempotencyKey => $"search-completed:{SearchId}";
}
