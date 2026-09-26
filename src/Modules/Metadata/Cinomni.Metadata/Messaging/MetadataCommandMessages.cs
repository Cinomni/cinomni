using Cinomni.Kernel.Messaging;
using Cinomni.Metadata.Contracts;

namespace Cinomni.Metadata.Messaging;

/// <summary>Stable registered names of the Metadata commands.</summary>
public static class MetadataCommandNames
{
    public const string RefreshMetadata = "metadata.refresh";
    public const string RefreshContinuingSeries = "metadata.refresh-continuing-series";
    public const string PurgeSnapshots = "metadata.purge-snapshots";
}

/// <summary>
/// Prunes superseded metadata snapshots and everything hanging off them. Parameterless — the
/// scheduler constructs it and the window is deployment configuration.
/// </summary>
public sealed record PurgeSnapshotsCommand : ICommand;

/// <summary>
/// Refreshes a work's metadata from a provider. The provider fetch is out-of-process, so the work must
/// be retryable and recoverable; the refresh service is idempotent per (work, provider).
/// <see cref="Kind"/> routes the fetch to a provider that covers that media kind.
/// </summary>
public sealed record RefreshMetadataCommand(
    Guid WorkId,
    string Provider,
    string ExternalId,
    MetadataMediaKind Kind = MetadataMediaKind.Movie) : ICommand;

/// <summary>
/// Periodic sweep (job <c>metadata.refresh-continuing-series</c>): enqueues a refresh for every series
/// whose last snapshot reports it as still producing episodes and whose short series TTL has lapsed.
/// Without it a weekly-airing show would never learn about a new episode until somebody posted
/// <c>/api/metadata/refresh</c> by hand — every monitoring decision downstream is made against the
/// structure the last snapshot left behind. Parameterless so the scheduler can instantiate it.
/// </summary>
public sealed record RefreshContinuingSeriesCommand : ICommand;
