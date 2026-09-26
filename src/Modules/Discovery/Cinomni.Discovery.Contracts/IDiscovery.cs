using Cinomni.Kernel.Results;
using Cinomni.Search.Contracts;

namespace Cinomni.Discovery.Contracts;

/// <summary>
/// Public search surface of the Discovery module: federate the configured indexers on a
/// neutral criterion and return deduplicated raw releases. It never decides quality.
/// </summary>
public interface IReleaseSearch
{
    /// <summary>
    /// Runs a federated search. <paramref name="origin"/> is recorded with the execution so a
    /// downstream module can read back what was asked for; it is empty for a manual API search.
    /// </summary>
    Task<SearchOutcome> SearchAsync(
        SearchCriterion criterion,
        SearchOrigin origin = default,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads the raw releases persisted for a past execution, so a downstream module (Release
/// Parsing) can pull them by id rather than reading Discovery's tables.
/// </summary>
public interface IReleaseSearchResults
{
    Task<IReadOnlyList<ReleaseCandidate>> GetResultsAsync(
        SearchExecutionId executionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// What the execution asked for (term, kind, numbering, external ids, requested units). Null
    /// when the execution is unknown. This is the published alternative to reaching into
    /// <c>discovery.search_executions</c>, which no other module may do.
    /// </summary>
    Task<SearchRequestContext?> GetRequestContextAsync(
        SearchExecutionId executionId,
        CancellationToken cancellationToken = default);
}

/// <summary>Configuration surface of the Discovery module: register and list indexers.</summary>
public interface IIndexerAdministration
{
    /// <summary>Every entry of every enabled catalog source, from its last successful refresh.</summary>
    Task<IReadOnlyList<IndexerCatalogEntry>> ListCatalogAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Installs one catalog entry as a definition indexer, snapshotting its definition. Idempotent per
    /// source and key: installing the same entry again returns the indexer already installed.
    /// </summary>
    Task<Result<IndexerId>> InstallCatalogIndexerAsync(
        IndexerCatalogSourceId sourceId,
        string key,
        string? name,
        string? baseUrl,
        int? priority,
        IndexerSettings? settings,
        CancellationToken cancellationToken = default);

    Task<Result<IndexerTestResult>> TestCatalogIndexerAsync(
        IndexerCatalogSourceId sourceId,
        string key,
        string? name,
        string? baseUrl,
        int? priority,
        IndexerSettings? settings,
        CancellationToken cancellationToken = default);

    Task<Result> SetSettingsAsync(
        IndexerId indexerId,
        IndexerSettings settings,
        CancellationToken cancellationToken = default);

    Task<Result<IndexerTestResult>> TestIndexerAsync(
        IndexerId indexerId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers an indexer. The base URL must be an absolute http(s) URL whose host is public
    /// (an internal/loopback/metadata host is rejected up front — SSRF defense).
    /// <paramref name="definitionId"/> is required when <paramref name="protocol"/> is
    /// <see cref="IndexerProtocol.Definition"/> and rejected otherwise.
    /// <paramref name="credential"/>, when given, is validated and stored in the same save as the
    /// indexer — an indexer that needs one never exists without it, and a refused credential
    /// (including an installation with no master key) adds nothing.
    /// </summary>
    Task<Result<IndexerId>> AddIndexerAsync(
        string name,
        IndexerProtocol protocol,
        string baseUrl,
        int priority,
        IndexerDefinitionId? definitionId = null,
        NewIndexerCredential? credential = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Every configured indexer, with the credential reported as an
    /// <see cref="IndexerCredentialState"/> rather than as a boolean: a caller has to be able to tell
    /// a credential this installation can use from one it merely stores and silently ignores. Never
    /// returns a secret in any form — see <see cref="SetCredentialAsync"/> for why there is no read.
    /// </summary>
    Task<IReadOnlyList<IndexerSummary>> ListIndexersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores an indexer's credential, replacing any existing one. Write-only on purpose: nothing in
    /// this interface reads a secret back, so changing one means supplying it again.
    /// </summary>
    /// <param name="username">
    /// The account name, or null for an API key. For a 'Definition' indexer it fills the declared
    /// login form; for Torznab/Newznab it switches from <c>apikey</c> to HTTP Basic, which is only
    /// accepted for an https base URL.
    /// </param>
    /// <param name="secret">The API key or password. Never returned, logged or echoed in an error.</param>
    Task<Result> SetCredentialAsync(
        IndexerId indexerId,
        string? username,
        string secret,
        CancellationToken cancellationToken = default);

    /// <summary>Removes an indexer's credential, leaving it to be queried unauthenticated.</summary>
    Task<Result> ClearCredentialAsync(IndexerId indexerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Turns an indexer on or off. A disabled indexer is not asked by a search. Setting the value it
    /// already has is a no-op success.
    /// </summary>
    Task<Result> SetEnabledAsync(IndexerId indexerId, bool enabled, CancellationToken cancellationToken = default);

    /// <summary>
    /// Changes the tie-break order. Lower wins when the same release comes from several indexers.
    /// The allowed range is the same one <see cref="AddIndexerAsync"/> accepts.
    /// </summary>
    Task<Result> SetPriorityAsync(IndexerId indexerId, int priority, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the indexer, its stored credential and its sign-in session. A definition document is
    /// not deleted, even when this indexer was the only one using it — another indexer may still
    /// reference it, and an operator can attach it again.
    /// </summary>
    Task<Result> DeleteIndexerAsync(IndexerId indexerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records what an endpoint can be asked (search modes, declared parameters, category ids).
    /// Until this is called the indexer is queried exactly as it was before capabilities existed —
    /// no <c>cat=</c> and every standard parameter — so configuring nothing changes nothing.
    /// </summary>
    Task<Result> SetCapabilitiesAsync(
        IndexerId indexerId,
        IndexerCapabilities capabilities,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates and persists a declarative indexer definition. The content is parsed with the
    /// same <c>IndexerDefinitionParser</c> a search would use, so an upload fails with the exact
    /// named error a bad definition would surface at search time, never after the fact.
    /// </summary>
    Task<Result<IndexerDefinitionId>> UploadDefinitionAsync(
        string name,
        string rawContent,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Dry-runs a definition without persisting it: always validates the format, and — when a
    /// sample response is supplied — additionally extracts what it would produce, for the
    /// console's "test this definition" affordance. Never issues a real HTTP request.
    /// <para>
    /// The result carries the candidates <em>and</em> every declared field rule that produced no
    /// value (<see cref="DefinitionExtractionResult.FieldIssues"/>). That is the point of the dry
    /// run: a rule that silently yields a neutral value looks exactly like a site that reports one,
    /// so without the issues an operator cannot tell a working definition from a broken one.
    /// </para>
    /// </summary>
    Task<Result<DefinitionExtractionResult>> ValidateDefinitionAsync(
        string rawContent,
        string? sampleResponseBody,
        string? sampleRequestUrl,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<IndexerDefinitionSummary>> ListDefinitionsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The catalog sources an administrator subscribes to. Cinomni ships with none: each is an HTTPS URL
/// the administrator chooses, publishing a versioned JSON manifest of indexer entries. The manifest
/// is hostile input: it is fetched over the SSRF-guarded transport with a bounded size and time, and
/// rejected whole when any entry fails validation, keeping the previous snapshot.
/// </summary>
public interface IIndexerCatalogSources
{
    Task<IReadOnlyList<IndexerCatalogSourceSummary>> ListSourcesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves the source, then refreshes it once. The source is kept even when that first refresh
    /// fails; the failure is reported in its <c>LastRefresh*</c> fields.
    /// </summary>
    Task<Result<IndexerCatalogSourceSummary>> AddSourceAsync(
        string name,
        string url,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches the manifest again. Succeeds whenever the source exists: a failed fetch or an invalid
    /// manifest is an outcome recorded on the source, not an error, and keeps the previous snapshot.
    /// </summary>
    Task<Result<IndexerCatalogSourceSummary>> RefreshSourceAsync(
        IndexerCatalogSourceId sourceId,
        CancellationToken cancellationToken = default);

    /// <summary>Renames or enables/disables a source. A disabled source's entries are not listed or installable.</summary>
    Task<Result<IndexerCatalogSourceSummary>> UpdateSourceAsync(
        IndexerCatalogSourceId sourceId,
        string name,
        bool enabled,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the source and its snapshot. Indexers installed from it stay installed and keep
    /// working: their definition was snapshotted at install time.
    /// </summary>
    Task<Result> DeleteSourceAsync(IndexerCatalogSourceId sourceId, CancellationToken cancellationToken = default);
}
