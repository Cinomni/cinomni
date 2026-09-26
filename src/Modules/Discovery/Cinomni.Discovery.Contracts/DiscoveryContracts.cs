using Cinomni.Kernel.Identifiers;

namespace Cinomni.Discovery.Contracts;

/// <summary>Stable internal identity of a configured indexer (UUIDv7).</summary>
public readonly record struct IndexerId(Guid Value)
{
    public static IndexerId New() => new(Uuid7.New());

    public override string ToString() => Value.ToString();
}

/// <summary>Stable internal identity of a search execution (UUIDv7).</summary>
public readonly record struct SearchExecutionId(Guid Value)
{
    public static SearchExecutionId New() => new(Uuid7.New());

    public override string ToString() => Value.ToString();
}

/// <summary>
/// Stable internal identity of an administrator-subscribed indexer catalog source (UUIDv7). A source
/// is an HTTPS URL publishing a JSON manifest of catalog entries; Cinomni itself ships none.
/// </summary>
public readonly record struct IndexerCatalogSourceId(Guid Value)
{
    public static IndexerCatalogSourceId New() => new(Uuid7.New());

    public override string ToString() => Value.ToString();
}

/// <summary>Stable internal identity of an uploaded indexer definition (UUIDv7).</summary>
public readonly record struct IndexerDefinitionId(Guid Value)
{
    public static IndexerDefinitionId New() => new(Uuid7.New());

    public override string ToString() => Value.ToString();
}

/// <summary>
/// The query protocol an indexer speaks. <see cref="Definition"/> covers a site with neither a
/// Torznab nor a Newznab API, driven by an operator-authored declarative definition instead of a
/// hardcoded adapter (own JSON format — see <c>IndexerDefinitionParser</c>; not an interpreter for
/// any third-party YAML dialect).
/// </summary>
public enum IndexerProtocol
{
    Torznab = 1,
    Newznab = 2,
    Definition = 3,
}

/// <summary>The transport of a release. The model carries both though the MVP downloads torrents only.</summary>
public enum ReleaseProtocol
{
    Torrent = 1,
    Usenet = 2,
}

/// <summary>
/// What one indexer can actually be asked. Torznab endpoints differ wildly: some have no
/// <c>t=tvsearch</c> at all, some reject an unknown parameter outright, and every one of them
/// numbers its categories differently.
/// <para>
/// <b>Unknown means "send what we have always sent".</b> A null/empty parameter list is read as
/// "not declared" rather than "not supported", and null categories mean no <c>cat=</c> is sent —
/// which is exactly the behaviour that shipped before capabilities existed, so configuring nothing
/// leaves an existing install byte-for-byte unchanged.
/// </para>
/// </summary>
/// <param name="MovieCategories">Indexer-local category ids for movies (e.g. 2000); empty sends no <c>cat=</c>.</param>
/// <param name="TvCategories">Indexer-local category ids for TV (e.g. 5000); empty sends no <c>cat=</c>.</param>
/// <param name="MovieSearchParams">Declared <c>t=movie</c> parameters (<c>q</c>, <c>year</c>, <c>imdbid</c>, <c>tmdbid</c>).</param>
/// <param name="TvSearchParams">Declared <c>t=tvsearch</c> parameters (<c>q</c>, <c>season</c>, <c>ep</c>, <c>tvdbid</c>, <c>imdbid</c>).</param>
public sealed record IndexerCapabilities(
    bool SupportsMovieSearch = true,
    bool SupportsTvSearch = true,
    IReadOnlyList<int>? MovieCategories = null,
    IReadOnlyList<int>? TvCategories = null,
    IReadOnlyList<string>? MovieSearchParams = null,
    IReadOnlyList<string>? TvSearchParams = null)
{
    /// <summary>Nothing was probed or configured: assume both modes and every standard parameter.</summary>
    public static readonly IndexerCapabilities Unknown = new();

    /// <summary>Torznab parameter names this type reasons about.</summary>
    public static class ParamNames
    {
        public const string Query = "q";
        public const string Year = "year";
        public const string ImdbId = "imdbid";
        public const string TmdbId = "tmdbid";
        public const string TvdbId = "tvdbid";
        public const string Season = "season";
        public const string Episode = "ep";
    }

    public bool SupportsMovieParam(string name) => Declares(MovieSearchParams, name);

    public bool SupportsTvParam(string name) => Declares(TvSearchParams, name);

    private static bool Declares(IReadOnlyList<string>? declared, string name) =>
        declared is null || declared.Count == 0
        || declared.Any(p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// A raw release returned by an indexer, deliberately unparsed: Discovery federates and
/// deduplicates but does not judge quality (that is Decision, via Release Parsing). The
/// <see cref="Guid"/> is the indexer-supplied identity used to deduplicate across indexers.
/// </summary>
/// <param name="SeasonNumber">Season the <em>indexer</em> attributed to the release, when it says so.</param>
/// <param name="EpisodeNumber">Episode the indexer attributed to the release, when it says so.</param>
/// <param name="TvdbId">TheTVDB series id the indexer attributed to the release, when it says so.</param>
/// <param name="Category">The indexer's own category id for the release, when it says so.</param>
/// <param name="Leechers">Peers downloading without a complete copy, when the indexer says so.</param>
/// <remarks>
/// The four numbering members are trailing optionals populated from the feed's
/// <c>&lt;torznab:attr&gt;</c>/<c>&lt;newznab:attr&gt;</c> elements. They are a far more reliable
/// signal than re-parsing the title, but they are advisory — most indexers omit them.
/// </remarks>
public sealed record ReleaseCandidate(
    string Guid,
    string Title,
    string DownloadUrl,
    ReleaseProtocol Protocol,
    long SizeBytes,
    int? Seeders,
    DateTimeOffset? PublishedAt,
    string IndexerName,
    int? SeasonNumber = null,
    int? EpisodeNumber = null,
    string? TvdbId = null,
    string? Category = null,
    int? Leechers = null);

/// <summary>
/// A credential supplied together with a new indexer. Write-only, like every credential path: it is
/// encrypted on arrival and never appears on a response.
/// </summary>
/// <param name="Secret">The API key, or the password when <paramref name="Username"/> is given.</param>
/// <param name="Username">The account name; null for an API key.</param>
public sealed record NewIndexerCredential(string Secret, string? Username = null);

/// <summary>
/// What this installation can currently say about one indexer's stored credential. The distinction
/// exists because "there are bytes in the row" and "this process can read them" are different facts:
/// a master key that is absent, rotated or lost leaves the ciphertext untouched and undecryptable, and
/// reporting that as "configured" tells an operator the opposite of the truth — every search against
/// that indexer is already running unauthenticated.
/// <para>
/// Every member names a deployment fact and never a value. The secret has no representation here, in
/// this enum or anywhere else on <see cref="IndexerSummary"/>, and none of these states is an
/// instruction to retry: the three failure classes below all degrade to an unauthenticated query,
/// exactly as <see cref="None"/> does.
/// </para>
/// </summary>
public enum IndexerCredentialState
{
    /// <summary>No credential is stored. The indexer is queried unauthenticated, as configured.</summary>
    None = 0,

    /// <summary>A credential is stored and this installation decrypted it. The only state that authenticates.</summary>
    Readable = 1,

    /// <summary>
    /// A credential is stored, but this installation has no master key at all (<c>CINOMNI_SECRET_KEY</c>
    /// absent or invalid). Setting the variable that wrote the row makes it readable again.
    /// </summary>
    MasterKeyMissing = 2,

    /// <summary>
    /// A credential is stored under a master key other than the one this installation holds — the key
    /// was rotated, or the database was restored somewhere that never had it. The stored bytes are not
    /// recoverable from here; the credential has to be entered again.
    /// </summary>
    MasterKeyChanged = 3,

    /// <summary>
    /// A credential is stored under the master key this installation holds, yet fails authentication:
    /// a corrupted row, or ciphertext that belongs to a different indexer (the additional-authenticated
    /// -data binding refusing a moved row). It has to be entered again.
    /// </summary>
    Corrupt = 4,
}

/// <summary>The reset period for indexer request limits. Closed for wire compatibility.</summary>
public enum IndexerLimitsUnit
{
    Day = 1,
}

/// <summary>
/// What this installation can currently say about one definition-backed indexer's login session. A
/// definition that declares a <c>session.login</c> block signs in with its stored credential before
/// searching; the state tells an operator whether that actually happened.
/// <para>
/// Every member names a session fact and never a value: no cookie, token or account material has any
/// representation here. <see cref="None"/> deliberately covers two different situations — an indexer
/// that needs no login, and one whose credential cannot be read (the credential state on the same
/// summary names which) — because from the session's point of view both mean "nothing to keep".
/// </para>
/// </summary>
public enum IndexerSessionState
{
    /// <summary>No usable session is kept: the definition declares no login, or its credential is unreadable.</summary>
    None = 0,

    /// <summary>
    /// The definition declares a login with a readable credential, but no session has been captured
    /// yet. The next search signs in.
    /// </summary>
    NotLoggedIn = 1,

    /// <summary>A login succeeded and its session is stored, ready for the next search.</summary>
    Active = 2,

    /// <summary>The last sign-in attempt failed; the indexer is searched unauthenticated until one succeeds.</summary>
    Failed = 3,
}

/// <summary>Operator-controlled behavior and request limits for one indexer.</summary>
public sealed record IndexerSettings(
    int? MinimumSeeders = null,
    bool PreferMagnet = false,
    int? QueryLimit = null,
    int? GrabLimit = null,
    IndexerLimitsUnit LimitsUnit = IndexerLimitsUnit.Day,
    bool UseFlareSolverr = false);

/// <summary>
/// One entry of a subscribed catalog source, as last fetched successfully. Cinomni ships no entries:
/// every one comes from a source an administrator added.
/// </summary>
/// <param name="Key">Unique within its source only; two sources may publish the same key.</param>
/// <param name="SourceId">The source that published it; install and test address the entry by both.</param>
/// <param name="SourceName">The administrator's name for that source, for display.</param>
public sealed record IndexerCatalogEntry(
    string Key,
    int Version,
    string Name,
    string Description,
    IndexerProtocol Protocol,
    ReleaseProtocol ReleaseProtocol,
    IReadOnlyList<string> BaseUrls,
    bool RequiresFlareSolverr,
    IndexerId? InstalledIndexerId,
    int DefaultPriority,
    IndexerSettings DefaultSettings,
    IndexerCatalogSourceId SourceId,
    string SourceName);

/// <summary>
/// A subscribed catalog source and the outcome of its last refresh. A failed refresh keeps the
/// previous snapshot, so <paramref name="EntryCount"/> is what is installable now, whatever
/// <paramref name="LastRefreshSucceeded"/> says.
/// </summary>
/// <param name="LastRefreshedAt">When a refresh last ran to an outcome; null before the first one.</param>
/// <param name="LastRefreshCode">
/// A bounded code: <c>discovery.catalog_source.refreshed</c> on success, otherwise the named
/// <c>discovery.catalog_source.*</c> failure (fetch_failed, too_large, invalid_manifest).
/// </param>
/// <param name="LastRefreshMessage">A sentence an administrator can act on; never a raw response body.</param>
public sealed record IndexerCatalogSourceSummary(
    IndexerCatalogSourceId Id,
    string Name,
    string Url,
    bool Enabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastRefreshedAt,
    bool? LastRefreshSucceeded,
    string? LastRefreshCode,
    string? LastRefreshMessage,
    int EntryCount);

/// <summary>Sanitized result of a live indexer check.</summary>
/// <param name="Authenticated">
/// Whether the check ran <em>with</em> the indexer's stored session, when the definition declares a
/// login and a readable credential; null when neither applies, because "authenticated" is not a
/// meaningful question for an indexer that needs no login. False means the sign-in failed and the
/// candidates, if any, are what the site shows an anonymous visitor.
/// </param>
public sealed record IndexerTestResult(
    bool Succeeded,
    string Code,
    string Message,
    int CandidateCount,
    long DurationMs,
    DateTimeOffset TestedAt,
    bool? Authenticated = null);

/// <summary>Minimal projection of a configured indexer exposed to the API.</summary>
/// <param name="Capabilities">
/// What this endpoint can be asked. Trailing optional; null is read as
/// <see cref="IndexerCapabilities.Unknown"/>.
/// </param>
/// <param name="DefinitionId">
/// The declarative definition this indexer runs on. Only meaningful when
/// <see cref="Protocol"/> is <see cref="IndexerProtocol.Definition"/>; null otherwise.
/// </param>
/// <param name="CredentialUsername">
/// The account this indexer authenticates as, or null when it has none. An identifier, not a secret —
/// it is what lets an operator tell a wrong account from an expired password.
/// </param>
/// <param name="CredentialState">
/// Whether a secret is stored <em>and whether this installation can read it</em>. The secret itself
/// has no representation on this record at all: it travels to an adapter as a separate
/// <c>IndexerCredential</c>, precisely because this record is also an HTTP response body.
/// </param>
/// <param name="DeclaresLogin">
/// Whether this indexer's definition declares a <c>session.login</c> block at all — the fact behind
/// the session, stated rather than left to be inferred. <see cref="SessionState"/> alone cannot
/// answer it: 'None' is also what an indexer with a login but an unreadable credential reports, so a
/// caller inferring "needs no login" from it would be wrong exactly when a credential is broken.
/// </param>
/// <param name="SessionState">
/// Whether the indexer's declared login actually produced a kept session. Read together with
/// <see cref="CredentialState"/>: 'None' with a 'Readable' credential and a 'Definition' protocol
/// means the definition declares no login at all, while 'NotLoggedIn' means it does and the next
/// search will sign in.
/// </param>
/// <param name="LastLoginAt">
/// When the currently kept session was captured, or null when there is none. A session that stops
/// authenticating is dropped and re-created by the next search, which moves this timestamp — it is
/// how an operator tells a kept session from a site that keeps expiring it.
/// </param>
/// <param name="CatalogSourceId">
/// The catalog source this indexer was installed from, or null: it was added by hand, installed from
/// the catalog that earlier versions shipped built in, or its source has since been removed. Null
/// leaves it an ordinary definition indexer; it never stops it working.
/// </param>
public sealed record IndexerSummary(
    IndexerId Id,
    string Name,
    IndexerProtocol Protocol,
    string BaseUrl,
    int Priority,
    bool Enabled,
    IndexerCapabilities? Capabilities = null,
    IndexerDefinitionId? DefinitionId = null,
    string? CredentialUsername = null,
    IndexerCredentialState CredentialState = IndexerCredentialState.None,
    IndexerSettings? Settings = null,
    string? CatalogKey = null,
    int? CatalogVersion = null,
    DateTimeOffset? LastTestedAt = null,
    bool? LastTestSucceeded = null,
    string? LastTestCode = null,
    string? LastTestMessage = null,
    bool DeclaresLogin = false,
    IndexerSessionState SessionState = IndexerSessionState.None,
    DateTimeOffset? LastLoginAt = null,
    IndexerCatalogSourceId? CatalogSourceId = null);

/// <summary>
/// One declared field rule of one result row that did not produce a value. This is what keeps a rule
/// that failed distinguishable from a site that genuinely says "0 bytes" and from a definition that
/// declares no such rule at all: only a declared rule can be reported here, and a rule that worked
/// never is.
/// </summary>
/// <param name="RowIndex">Zero-based position of the row within the response, as extracted.</param>
/// <param name="Field">
/// The definition's own field name — <c>title</c>, <c>downloadUrl</c>, <c>sizeBytes</c>,
/// <c>seeders</c> or <c>publishedAt</c> — so it names the rule an operator has to go and edit.
/// </param>
/// <param name="RawValue">
/// What the selector actually produced, truncated. Null when it produced nothing at all, which is
/// itself the answer to "is my selector wrong, or is my transform wrong?".
/// </param>
/// <param name="Code">The named failure, e.g. <c>discovery.definition.transform.parse_size_failed</c>.</param>
public sealed record DefinitionFieldIssue(
    int RowIndex,
    string Field,
    string? RawValue,
    string Code,
    string Message);

/// <summary>
/// What a definition extracted from one response: the candidates it produced, plus every declared
/// field rule that did not produce a value. A candidate is never withheld because of an issue — an
/// unreadable size still yields the release with a size of zero — so the two lists describe the same
/// rows from two angles, and the dry run shows exactly what a real search would have produced.
/// <para>
/// Both lists are bounded by the definition's own validated row ceiling: at most one issue per
/// declared field rule per extracted row, each carrying a truncated raw value.
/// </para>
/// </summary>
public sealed record DefinitionExtractionResult(
    IReadOnlyList<ReleaseCandidate> Candidates,
    IReadOnlyList<DefinitionFieldIssue> FieldIssues);

/// <summary>An uploaded indexer definition, as listed for an administrator — never its rules/content.</summary>
public sealed record IndexerDefinitionSummary(
    IndexerDefinitionId Id,
    string Name,
    int SchemaVersion,
    string ContentHash,
    DateTimeOffset CreatedAt);

/// <summary>The result of a federated search: the execution id plus the deduplicated candidates.</summary>
public sealed record SearchOutcome(
    SearchExecutionId ExecutionId,
    IReadOnlyList<ReleaseCandidate> Candidates);

/// <summary>
/// Who asked for a search, so Discovery can record the correlation without knowing what a Work or a
/// monitored target <em>is</em>. Every member is optional: a manual search from the API has no origin.
/// </summary>
public readonly record struct SearchOrigin(
    Guid? TargetId = null,
    Guid? WorkId = null,
    IReadOnlyList<Guid>? UnitIds = null);

/// <summary>
/// What a past search actually asked for. Decision reads this back to check that a candidate is the
/// thing that was requested — it must never read <c>discovery.search_executions</c> directly.
/// </summary>
/// <param name="RequestedUnitIds">
/// The catalog units the search was trying to acquire. Empty when the caller supplied none, which
/// means "the whole target".
/// </param>
public sealed record SearchRequestContext(
    SearchExecutionId ExecutionId,
    string Term,
    int? Year,
    string ContentKind,
    Guid? TargetId,
    Guid? WorkId,
    int? SeasonNumber,
    int? EpisodeNumber,
    int? AbsoluteNumber,
    DateOnly? AirDate,
    string? TvdbId,
    string? ImdbId,
    IReadOnlyList<Guid> RequestedUnitIds);
