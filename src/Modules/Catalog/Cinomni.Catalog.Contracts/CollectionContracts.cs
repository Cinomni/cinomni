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

// THE AGREED CONTRACT FOR RULE-BASED COLLECTIONS. Recorded beside the engine because the engine is
// built and the surface around it is not. Every point below was settled before implementation and
// should not be re-derived.
//
// - A work lives in exactly one collection, and a collection decides who may see it. Rules therefore
//   do not produce overlapping views: they decide the single collection a work belongs to. Moving a
//   work by rule is a permissions change wearing the shape of an organising one.
//
// - Order is global and deterministic. Rules hang off collections; CollectionSummary.RulePriority
//   fixes evaluation order across the installation, and the first rule that matches claims the work.
//
// - A work no rule claims returns to the default collection, unless it is pinned. If a rule placed
//   it, rules govern it: leaving it on a shelf whose own rules exclude it would make "why is this
//   here" unanswerable. The default collection is open, so this case is an exposure event, and any
//   preview has to show it — it is the one nobody anticipates, because it comes from a work ceasing
//   to match rather than from anyone moving it.
//
// - A manual move pins the work, and rules stop touching it until it is unpinned. An override the
//   next sweep undoes is not an override.
//
// - The work records which rule placed it. That answer decides who sees it, so it is persisted
//   rather than logged, on the same reasoning as Decision's evaluations.
//
// - Preview before saving, for both writes. Saving rules and reordering priority can each relocate
//   hundreds of works and change who sees them. The preview reports what would move, what is pinned
//   and therefore will not, and where each work would end up. Reordering earns this as much as
//   editing does — arguably more, since it only affects works matched by more than one collection,
//   which is precisely the set nobody can see.
//
// - Re-evaluation runs on work added, on metadata enrichment, and on any rule or order change.
//   Enrichment is not optional: genres arrive after the work does, so a rule on genre would never
//   match without it. The bulk sweep has to be recoverable and batched, never one unprotected
//   transaction across the library.
//
// - The surface, administrator only: GET and PUT /api/catalog/collections/{id}/rules; PUT
//   /api/catalog/collections/rule-priority, taking the complete ordered list so ties are impossible
//   and no intermediate order is ever visible; POST on both of those with /preview; PUT
//   /api/catalog/works/{id}/collection, which moves and pins; DELETE
//   /api/catalog/works/{id}/collection-pin. A preview answers matched, wouldMove, pinnedSkipped and
//   the works themselves with their current and target collection.

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

/// <summary>A collection as listed: what it is, who may see it, and how much it holds.</summary>
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

    /// <summary>
    /// Moves a work by hand and pins it there: the rules leave a pinned work alone until
    /// <see cref="ICollectionRules.UnpinWorkAsync"/> releases it.
    /// </summary>
    Task<Result> MoveWorkAsync(WorkId workId, CollectionId collectionId, CancellationToken cancellationToken = default);
}

/// <summary>
/// One rule as an administrator submits it. <see cref="Id"/> is the rule being kept when it names one
/// of the collection's current rules, and a new rule otherwise — the set is replaced whole, so a rule
/// left out is a rule deleted.
/// </summary>
public sealed record CollectionRuleDraft(
    Guid? Id,
    string Name,
    IReadOnlyList<CollectionRuleCondition> Conditions);

/// <summary>One title a preview reports: where it is now, and where the proposed rules would put it.</summary>
/// <param name="Pinned">
/// True when it was moved by hand and the rules will therefore leave it where it is; it is listed so
/// the operator can see the override they are not overriding.
/// </param>
public sealed record RulePreviewWork(
    WorkId Id,
    string Title,
    int? Year,
    WorkKind Kind,
    CollectionId CurrentCollectionId,
    string CurrentCollectionName,
    CollectionId TargetCollectionId,
    string TargetCollectionName,
    bool Pinned);

/// <summary>
/// What a rule change would do, before it does it. The three counts are not interchangeable: a pinned
/// title matches and stays put, and a title already on its target shelf matches and does not move.
/// </summary>
/// <param name="Matched">The titles the proposed rules match, whether or not that moves them.</param>
/// <param name="WouldMove">The titles that would change collection, and therefore audience.</param>
/// <param name="PinnedSkipped">The titles the rules would move but a manual pin holds in place.</param>
/// <param name="Works">
/// The affected titles, moving ones first, capped at <see cref="MaxWorks"/>; the counts cover them all.
/// </param>
public sealed record RulePreview(
    int Matched,
    int WouldMove,
    int PinnedSkipped,
    IReadOnlyList<RulePreviewWork> Works)
{
    public const int MaxWorks = 100;
}

/// <summary>How many titles changed collection when a rule change was applied.</summary>
public sealed record RulesApplied(int Moved);

/// <summary>
/// Operator surface for rule-based placement: the rules each collection carries, the order collections
/// are asked in, a preview of both before they are saved, and the manual pin that takes a title out of
/// the rules' reach. Every write here can change who may see a title, which is why each has a preview.
/// </summary>
public interface ICollectionRules
{
    /// <summary>The collection's rules in evaluation order, or a not-found failure.</summary>
    Task<Result<IReadOnlyList<CollectionRule>>> RulesAsync(CollectionId id, CancellationToken cancellationToken = default);

    /// <summary>What replacing the collection's rules with <paramref name="rules"/> would do. Nothing is saved.</summary>
    Task<Result<RulePreview>> PreviewRulesAsync(
        CollectionId id,
        IReadOnlyList<CollectionRuleDraft> rules,
        CancellationToken cancellationToken = default);

    /// <summary>Replaces the collection's rules and re-places the library under them.</summary>
    Task<Result<RulesApplied>> SetRulesAsync(
        CollectionId id,
        IReadOnlyList<CollectionRuleDraft> rules,
        CancellationToken cancellationToken = default);

    /// <summary>What asking the collections in this order would do. Nothing is saved.</summary>
    Task<Result<RulePreview>> PreviewRulePriorityAsync(
        IReadOnlyList<CollectionId> order,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the evaluation order. <paramref name="order"/> must name every collection exactly once, so no
    /// two can tie and no partial order is ever stored.
    /// </summary>
    Task<Result<RulesApplied>> SetRulePriorityAsync(
        IReadOnlyList<CollectionId> order,
        CancellationToken cancellationToken = default);

    /// <summary>Releases a manual pin and places the title where the rules now say.</summary>
    Task<Result> UnpinWorkAsync(WorkId workId, CancellationToken cancellationToken = default);
}
