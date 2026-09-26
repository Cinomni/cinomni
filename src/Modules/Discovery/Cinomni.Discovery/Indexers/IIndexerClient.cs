using Cinomni.Discovery.Contracts;
using Cinomni.Search.Contracts;

namespace Cinomni.Discovery.Indexers;

/// <summary>
/// Port to a single indexer: query it for a neutral criterion and return raw releases. The
/// production adapter speaks Torznab/Newznab over HTTP; tests substitute an in-memory fake.
/// Implementations must be safe to call concurrently (the search fans out over indexers).
/// <para>
/// Concurrency is not a courtesy here, it is the contract: <c>ReleaseSearch</c> calls every enabled
/// indexer through this port inside one <c>Task.WhenAll</c>, and the module's scoped
/// <c>DiscoveryDbContext</c> — like the single Npgsql connection under it — is not thread-safe. An
/// implementation must therefore do <b>no database work of its own</b>; everything it needs from the
/// database is resolved by the caller before the fan-out and handed in as an argument.
/// </para>
/// </summary>
public interface IIndexerClient
{
    /// <param name="credential">
    /// The indexer's stored credential, or null when it has none, when this installation has no
    /// master key, or when the stored row could not be decrypted. An adapter treats all three the
    /// same way — query unauthenticated and let the endpoint answer — because they are the same
    /// situation from here: there is no usable secret to send.
    /// </param>
    /// <param name="definitionContent">
    /// The raw declarative definition this indexer runs, already read from the database by the
    /// caller, or null when the indexer needs none (Torznab/Newznab) or when the definition it
    /// references has since been deleted. Passed alongside the summary rather than on it for the
    /// same reason the credential is: <see cref="IndexerSummary"/> is the record the administration
    /// endpoint serializes, and a definition document has no business on the wire.
    /// </param>
    Task<IReadOnlyList<ReleaseCandidate>> SearchAsync(
        IndexerSummary indexer,
        IndexerCredential? credential,
        string? definitionContent,
        SearchCriterion criterion,
        CancellationToken cancellationToken = default);
}
