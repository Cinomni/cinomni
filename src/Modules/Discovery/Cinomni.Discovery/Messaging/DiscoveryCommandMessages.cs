using Cinomni.Kernel.Messaging;
using Cinomni.Search.Contracts;

namespace Cinomni.Discovery.Messaging;

/// <summary>Stable registered names of the Discovery commands.</summary>
public static class DiscoveryCommandNames
{
    public const string ExecuteSearch = "discovery.execute-search";

    public const string PurgeSearches = "discovery.purge-searches";
}

/// <summary>
/// Monthly partition maintenance for the search history: creates the months about to be written and
/// drops the months that have aged out. Parameterless — the scheduler constructs it and the window
/// is deployment configuration.
/// </summary>
public sealed record PurgeSearchesCommand : ICommand;

/// <summary>
/// Runs a federated search off the request path (enqueued by the SearchRequested reaction).
/// Carries the neutral criterion and the originating target id for correlation in
/// <c>SearchCompleted</c>. The <c>execute-search:{targetId}:{window}</c> key deduplicates the
/// <b>enqueue</b> — one key per <em>search occasion</em>, because the key is spent for ever and a
/// coarser window would silently cap how often a target can be searched at all. A crash-retry after
/// results commit may still produce a fresh execution and a second best-effort
/// <c>SearchCompleted</c> — acceptable, since results are ephemeral and read by execution id.
/// </summary>
/// <param name="WorkId">
/// The catalog work searched for, echoed from <c>SearchRequested</c> and recorded on the execution
/// so Decision can resolve a release's numbering against the right work. Trailing optional: the
/// command queue persists this payload as jsonb, so an in-flight 1.x row deserializes with it null.
/// </param>
/// <param name="UnitIds">
/// The catalog units the search is trying to acquire, echoed from <c>SearchRequested</c>. Trailing
/// optional for the same wire-stability reason.
/// </param>
public sealed record ExecuteSearchCommand(
    SearchCriterion Criterion,
    Guid TargetId,
    Guid? WorkId = null,
    IReadOnlyList<Guid>? UnitIds = null) : ICommand;
