using Cinomni.Kernel.Identifiers;
using Cinomni.Kernel.Results;
using Cinomni.Kernel.Security;

namespace Cinomni.Catalog.Contracts;

/// <summary>Stable internal identity of a collection (UUIDv7).</summary>
public readonly record struct CollectionId(Guid Value)
{
    public static CollectionId New() => new(Uuid7.New());

    public override string ToString() => Value.ToString();
}

/// <summary>What a collection holds. A shelf of movies, of series, or of both.</summary>
public enum CollectionKind
{
    Movies = 1,
    Series = 2,
    Mixed = 3,
}

/// <summary>
/// Who may browse a collection. <see cref="Open"/> is everyone signed in — which is what the default
/// collection is, so an upgrade changes nothing for an existing household. <see cref="Restricted"/> is
/// granted accounts only. Administrators see both.
/// </summary>
public enum CollectionAccessMode
{
    Open = 1,
    Restricted = 2,
}

/// <summary>A collection as listed: what it is, who may see it, and how much it holds.</summary>
/// <summary>
/// The closed vocabulary of work fields a collection rule may match on.
/// <para>
/// Availability, status and episode counts are deliberately absent. A rule over them would move a
/// work between collections as a side effect of a download finishing — and a collection decides who
/// may see a work, so a restricted shelf that opens itself when an episode lands would be a security
/// failure wearing the shape of a feature.
/// </para>
/// </summary>
public enum CollectionRuleField
{
    Kind = 1,
    Genre = 2,
    ContentRating = 3,
    Year = 4,
    RuntimeMinutes = 5,
    OriginalLanguage = 6,
    Title = 7,
}

/// <summary>How a condition compares. Which of these a field accepts is fixed; see the validator.</summary>
public enum CollectionRuleOperator
{
    Is = 1,
    IsNot = 2,
    AtLeast = 3,
    AtMost = 4,
    Contains = 5,
    StartsWith = 6,
}

/// <summary>
/// One condition of a rule. <see cref="Values"/> are alternatives: the condition holds when the work
/// satisfies any of them. Everything travels as a string, including numbers, and the backend parses
/// per field — one wire shape, and an unparseable value is refused when the rule is saved rather than
/// silently matching nothing months later.
/// </summary>
public sealed record CollectionRuleCondition(
    CollectionRuleField Field,
    CollectionRuleOperator Operator,
    IReadOnlyList<string> Values);

/// <summary>
/// A named set of conditions that all have to hold. Anyone needing "or" across different fields
/// writes a second rule; there is no nesting, which is what keeps the vocabulary one a validator can
/// fully understand and a rule builder can offer without proposing what the API would reject.
/// </summary>
public sealed record CollectionRule(
    Guid Id,
    CollectionId CollectionId,
    string Name,
    IReadOnlyList<CollectionRuleCondition> Conditions);

/// <param name="RulePriority">
/// Evaluation order across the installation; lower goes first, and the first rule that matches claims
/// the work. It exists because a work lives in exactly one collection, so two rules matching the same
/// work need a settled answer rather than whichever query returned first.
/// </param>
public sealed record CollectionSummary(
    CollectionId Id,
    string Name,
    CollectionKind Kind,
    CollectionAccessMode AccessMode,
    bool IsDefault,
    int WorkCount,
    int RulePriority = 0);

/// <summary>
/// The one authority on "may this account see this work". Catalog owns it because Catalog owns all three
/// facts the answer joins — where a work sits, what that collection allows, and who was granted it — so
/// the decision is a single indexed query with no cross-module hop and no eventual-consistency window.
/// Every module that serves content to a person consults this; nothing re-implements it.
/// </summary>
public interface IContentAccess
{
    /// <summary>The collections this viewer may browse. An administrator gets all of them.</summary>
    Task<IReadOnlyList<CollectionId>> VisibleCollectionsAsync(Viewer viewer, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the viewer may see this work. False for a work that does not exist: unknown reads exactly
    /// like not-yours, so a 404 tells an attacker nothing.
    /// </summary>
    Task<bool> CanSeeWorkAsync(Viewer viewer, Guid workId, CancellationToken cancellationToken = default);

    /// <summary>Narrows a set of work ids to the visible ones in one query — for a caller holding a page.</summary>
    Task<IReadOnlySet<Guid>> FilterWorksAsync(
        Viewer viewer,
        IReadOnlyCollection<Guid> workIds,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The catalog read model answered for one viewer — the scoped twin of <see cref="ICatalogQuery"/>.
/// Endpoints bind this one; the unscoped twin exists for event and command handlers, which have no
/// person to answer for.
/// </summary>
public interface ICatalogBrowse
{
    Task<IReadOnlyList<WorkSummary>> ListAsync(
        Viewer viewer,
        CollectionId? collection = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// One page of the works the viewer may see, narrowed by <paramref name="query"/> and in its order, with
    /// the total that matches so a client can walk the list without loading it all. <paramref name="limit"/>
    /// is clamped by <see cref="CatalogPaging.Clamp"/>, and a negative <paramref name="offset"/> reads as 0.
    /// </summary>
    Task<WorkPage> PageAsync(
        Viewer viewer,
        WorkListQuery query,
        int offset,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Counts for the list's filter controls over what the viewer may see on <paramref name="collection"/>:
    /// movies and series, and the genres of the works of <paramref name="kind"/> (all kinds when null).
    /// </summary>
    Task<WorkFacets> FacetsAsync(
        Viewer viewer,
        CollectionId? collection,
        WorkKind? kind,
        CancellationToken cancellationToken = default);

    Task<WorkSummary?> GetByIdAsync(Viewer viewer, WorkId id, CancellationToken cancellationToken = default);

    Task<WorkSummary?> FindByExternalIdAsync(
        Viewer viewer,
        MetadataProvider provider,
        string value,
        WorkKind? kind = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CollectionSummary>> CollectionsAsync(Viewer viewer, CancellationToken cancellationToken = default);
}

/// <summary>Operator surface: the collections themselves, who may see them, and what sits in them.</summary>
public interface ICollectionAdministration
{
    Task<IReadOnlyList<CollectionSummary>> ListAsync(CancellationToken cancellationToken = default);

    Task<Result<CollectionId>> CreateAsync(
        string name,
        CollectionKind kind,
        CollectionAccessMode accessMode,
        CancellationToken cancellationToken = default);

    Task<Result> SetAccessModeAsync(CollectionId id, CollectionAccessMode accessMode, CancellationToken cancellationToken = default);

    /// <summary>The accounts granted a restricted collection (ids only — Catalog does not own accounts).</summary>
    Task<IReadOnlyList<Guid>> GrantedAccountsAsync(CollectionId id, CancellationToken cancellationToken = default);

    Task<Result> GrantAsync(CollectionId id, Guid userId, Guid grantedByUserId, CancellationToken cancellationToken = default);

    Task<Result> RevokeAsync(CollectionId id, Guid userId, CancellationToken cancellationToken = default);

    Task<Result> MoveWorkAsync(WorkId workId, CollectionId collectionId, CancellationToken cancellationToken = default);
}
