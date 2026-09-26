using System.Globalization;
using Cinomni.Discovery.Contracts;

namespace Cinomni.Discovery.Persistence;

/// <summary>
/// The Discovery aggregate root: a configured Torznab/Newznab indexer. Health/backoff is a
/// separate concern added in a later increment; what the endpoint can be <em>asked</em> lives here,
/// because the query shape depends on it, and so does the credential it must be asked <em>with</em>.
/// </summary>
/// <remarks>
/// The capability columns are stored as comma-separated scalars rather than child tables: they are
/// short, always read whole, and never queried by element. Every one of them is nullable and read as
/// "not declared", so an indexer registered before capabilities existed is queried exactly as it was.
/// </remarks>
public sealed class Indexer
{
    private const char ListSeparator = ',';

    /// <summary>Column widths, shared with the model so a check and its column cannot disagree.</summary>
    public const int NameMaxLength = 200;

    public const int BaseUrlMaxLength = 1000;

    public const int CapabilityListMaxLength = 200;

    public const int CredentialUsernameMaxLength = 200;

    /// <summary>
    /// Why these capabilities cannot be stored, or null when they can: a list longer than its column,
    /// or a search parameter carrying the separator, which would come back as two parameters.
    /// </summary>
    public static string? CapabilityProblem(IndexerCapabilities capabilities)
    {
        var parameters = (capabilities.MovieSearchParams ?? []).Concat(capabilities.TvSearchParams ?? []);
        if (parameters.Any(p => p is not null && p.Contains(ListSeparator, StringComparison.Ordinal)))
        {
            return $"A search parameter must not contain '{ListSeparator}'.";
        }

        var lists = new (string Name, string? Joined)[]
        {
            ("movieCategories", JoinInts(capabilities.MovieCategories)),
            ("tvCategories", JoinInts(capabilities.TvCategories)),
            ("movieSearchParams", JoinStrings(capabilities.MovieSearchParams)),
            ("tvSearchParams", JoinStrings(capabilities.TvSearchParams)),
        };

        foreach (var (name, joined) in lists)
        {
            if (joined is { Length: > CapabilityListMaxLength })
            {
                return $"'{name}' is longer than {CapabilityListMaxLength} characters once joined.";
            }
        }

        return null;
    }

    public Guid Id { get; init; }

    public required string Name { get; set; }

    public IndexerProtocol Protocol { get; init; }

    public required string BaseUrl { get; set; }

    /// <summary>Lower wins when the same release comes from several indexers (dedup tie-break).</summary>
    public int Priority { get; set; }

    public bool Enabled { get; set; }

    /// <summary>Whether the endpoint answers <c>t=movie</c>; false falls the query back to <c>t=search</c>.</summary>
    public bool SupportsMovieSearch { get; set; } = true;

    /// <summary>Whether the endpoint answers <c>t=tvsearch</c>; false falls the query back to <c>t=search</c>.</summary>
    public bool SupportsTvSearch { get; set; } = true;

    /// <summary>Indexer-local movie category ids, comma separated. Null/empty sends no <c>cat=</c>.</summary>
    public string? MovieCategories { get; set; }

    /// <summary>Indexer-local TV category ids, comma separated. Null/empty sends no <c>cat=</c>.</summary>
    public string? TvCategories { get; set; }

    /// <summary>Declared <c>t=movie</c> parameters, comma separated. Null/empty means "not declared".</summary>
    public string? MovieSearchParams { get; set; }

    /// <summary>Declared <c>t=tvsearch</c> parameters, comma separated. Null/empty means "not declared".</summary>
    public string? TvSearchParams { get; set; }

    /// <summary>The declarative definition this indexer runs on. Set only when <see cref="Protocol"/> is <see cref="IndexerProtocol.Definition"/>.</summary>
    public Guid? DefinitionId { get; set; }

    /// <summary>
    /// The catalog entry key this indexer was installed from. Unique together with
    /// <see cref="CatalogSourceId"/>; an indexer installed from the catalog earlier versions shipped
    /// built in keeps its key with no source, and behaves as a plain definition indexer.
    /// </summary>
    public string? CatalogKey { get; set; }

    public int? CatalogVersion { get; set; }

    /// <summary>
    /// The catalog source it was installed from. Set to null when that source is removed: the
    /// indexer stays, running on the definition snapshotted when it was installed.
    /// </summary>
    public Guid? CatalogSourceId { get; set; }

    public int? MinimumSeeders { get; set; }

    public bool PreferMagnet { get; set; }

    public int? QueryLimit { get; set; }

    public int? GrabLimit { get; set; }

    public IndexerLimitsUnit LimitsUnit { get; set; } = IndexerLimitsUnit.Day;

    public bool UseFlareSolverr { get; set; }

    public DateTimeOffset? LastTestedAt { get; set; }

    public bool? LastTestSucceeded { get; set; }

    public string? LastTestCode { get; set; }

    public string? LastTestMessage { get; set; }

    /// <summary>
    /// The account name this indexer authenticates as, in plaintext and deliberately so: it is an
    /// identifier rather than a secret, and an operator who cannot see which account is configured
    /// cannot tell a wrong one from an expired one. Null for an API-key indexer, which has no user.
    /// </summary>
    public string? CredentialUsername { get; private set; }

    /// <summary>
    /// AES-256-GCM ciphertext with its tag appended, exactly as <c>SettingsSecretCipher</c> produces it.
    /// <para>
    /// Populated is not the same as usable, and nothing outside this module may read it as if it were:
    /// a row written under a master key this installation no longer holds is exactly as populated as a
    /// working one. <c>IndexerCredentialProtector.Classify</c> is the only thing that answers that
    /// question, and the projection reports its answer rather than the presence of these bytes.
    /// </para>
    /// </summary>
    public byte[]? SecretCipher { get; private set; }

    public byte[]? SecretNonce { get; private set; }

    /// <summary>Which master key encrypted it, so a rotated key is reported rather than decrypted into noise.</summary>
    public string? SecretKeyId { get; private set; }

    public DateTimeOffset CreatedAt { get; init; }

    public IndexerSettings ToSettings() => new(
        MinimumSeeders, PreferMagnet, QueryLimit, GrabLimit, LimitsUnit, UseFlareSolverr);

    /// <summary>
    /// Replaces the stored credential. Writing the four fields together is what keeps them coherent —
    /// the database's check constraint enforces cipher-and-nonce, but only this method guarantees a
    /// stale key id never survives a re-encryption.
    /// </summary>
    public void SetCredential(string? username, byte[] cipher, byte[] nonce, string keyId)
    {
        CredentialUsername = string.IsNullOrWhiteSpace(username) ? null : username.Trim();
        SecretCipher = cipher;
        SecretNonce = nonce;
        SecretKeyId = keyId;
    }

    /// <summary>Removes the credential entirely, leaving the indexer to be queried unauthenticated.</summary>
    public void ClearCredential()
    {
        CredentialUsername = null;
        SecretCipher = null;
        SecretNonce = null;
        SecretKeyId = null;
    }

    public IndexerCapabilities ToCapabilities() => new(
        SupportsMovieSearch,
        SupportsTvSearch,
        ParseInts(MovieCategories),
        ParseInts(TvCategories),
        ParseStrings(MovieSearchParams),
        ParseStrings(TvSearchParams));

    public void ApplyCapabilities(IndexerCapabilities capabilities)
    {
        SupportsMovieSearch = capabilities.SupportsMovieSearch;
        SupportsTvSearch = capabilities.SupportsTvSearch;
        MovieCategories = JoinInts(capabilities.MovieCategories);
        TvCategories = JoinInts(capabilities.TvCategories);
        MovieSearchParams = JoinStrings(capabilities.MovieSearchParams);
        TvSearchParams = JoinStrings(capabilities.TvSearchParams);
    }

    private static IReadOnlyList<int>? ParseInts(string? value)
    {
        var parts = ParseStrings(value);
        if (parts is null)
        {
            return null;
        }

        var numbers = new List<int>(parts.Count);
        foreach (var part in parts)
        {
            if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                numbers.Add(parsed);
            }
        }

        return numbers.Count > 0 ? numbers : null;
    }

    private static IReadOnlyList<string>? ParseStrings(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var parts = value.Split(ListSeparator, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts : null;
    }

    private static string? JoinInts(IReadOnlyList<int>? values) =>
        values is { Count: > 0 }
            ? string.Join(ListSeparator, values.Select(v => v.ToString(CultureInfo.InvariantCulture)))
            : null;

    private static string? JoinStrings(IReadOnlyList<string>? values)
    {
        var cleaned = values?.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).ToList();
        return cleaned is { Count: > 0 } ? string.Join(ListSeparator, cleaned) : null;
    }
}
