using Cinomni.Kernel.Identifiers;

namespace Cinomni.Metadata.Persistence;

/// <summary>The refresh lifecycle of a work's metadata from one provider.</summary>
public enum MetadataRefreshStatus
{
    Idle = 1,
    Refreshing = 2,
    Fresh = 3,
    Failed = 4,
}

/// <summary>
/// Tracks the metadata-refresh FSM for a work+provider. Pure, guarded transitions:
/// Idle → Refreshing → Fresh | Failed. A failure backs off (the work persists without a fresh
/// snapshot); a stale Fresh becomes eligible again after the TTL. Keyed by (work, provider) — the
/// get-or-create idempotency key.
/// </summary>
public sealed class RefreshState
{
    private const int ProviderMaxLength = 200;

    public Guid Id { get; init; }

    public Guid WorkId { get; init; }

    public required string Provider { get; init; }

    public MetadataRefreshStatus Status { get; private set; }

    /// <summary>How many refresh attempts have run (drives the failure backoff).</summary>
    public int Attempts { get; private set; }

    /// <summary>The snapshot that landed, once fresh.</summary>
    public Guid? SnapshotId { get; private set; }

    public DateTimeOffset? LastRefreshedAt { get; private set; }

    /// <summary>When a failed refresh becomes eligible to retry (the backoff horizon).</summary>
    public DateTimeOffset? NextEligibleAt { get; private set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static RefreshState Create(Guid workId, string provider, DateTimeOffset now) => new()
    {
        Id = Uuid7.New(),
        WorkId = workId,
        Provider = Text.Truncate(provider, ProviderMaxLength)!,
        Status = MetadataRefreshStatus.Idle,
        CreatedAt = now,
        UpdatedAt = now,
    };

    /// <summary>Whether a refresh should run now: never fetched, stale past the TTL, or past a failure backoff.</summary>
    public bool ShouldRefresh(DateTimeOffset now, TimeSpan ttl) => Status switch
    {
        MetadataRefreshStatus.Idle => true,
        MetadataRefreshStatus.Refreshing => false,
        MetadataRefreshStatus.Fresh => LastRefreshedAt is null || now - LastRefreshedAt.Value >= ttl,
        MetadataRefreshStatus.Failed => NextEligibleAt is null || now >= NextEligibleAt.Value,
        _ => false,
    };

    /// <summary>Idle/Fresh/Failed → Refreshing: opens an attempt.</summary>
    public void BeginRefresh(DateTimeOffset now)
    {
        Require(MetadataRefreshStatus.Idle, MetadataRefreshStatus.Fresh, MetadataRefreshStatus.Failed);
        Attempts += 1;
        Transition(MetadataRefreshStatus.Refreshing, now);
    }

    /// <summary>
    /// Refreshing → Fresh: a snapshot landed. Clears the backoff and resets the attempt counter, so the
    /// escalating backoff and the degradation threshold count <em>consecutive</em> failures — a later blip
    /// starts fresh at the base backoff, not wherever the lifetime count left off.
    /// </summary>
    public void MarkFresh(Guid snapshotId, DateTimeOffset now)
    {
        Require(MetadataRefreshStatus.Refreshing);
        SnapshotId = snapshotId;
        LastRefreshedAt = now;
        NextEligibleAt = null;
        Attempts = 0;
        Transition(MetadataRefreshStatus.Fresh, now);
    }

    /// <summary>Refreshing → Failed: the provider was unavailable; backs off until <paramref name="nextEligibleAt"/>.</summary>
    public void MarkFailed(DateTimeOffset nextEligibleAt, DateTimeOffset now)
    {
        Require(MetadataRefreshStatus.Refreshing);
        NextEligibleAt = nextEligibleAt;
        Transition(MetadataRefreshStatus.Failed, now);
    }

    private void Require(params MetadataRefreshStatus[] valid)
    {
        if (Array.IndexOf(valid, Status) < 0)
        {
            throw new InvalidOperationException(
                $"Illegal metadata-refresh transition from {Status} for work {WorkId} (expected one of {string.Join(",", valid)}).");
        }
    }

    private void Transition(MetadataRefreshStatus to, DateTimeOffset now)
    {
        Status = to;
        UpdatedAt = now;
    }
}
