using Cinomni.Catalog.Contracts;
using Cinomni.Catalog.Persistence;

namespace Cinomni.Catalog.Application;

/// <summary>One rule in the installation-wide evaluation order, with what its collection may hold.</summary>
public sealed record RankedRule(
    Guid RuleId,
    Guid CollectionId,
    CollectionKind CollectionKind,
    IReadOnlyList<CollectionRuleCondition> Conditions);

/// <summary>Where the rules put a work, and which rule did it — null when none claimed it.</summary>
public readonly record struct Placement(Guid CollectionId, Guid? RuleId);

/// <summary>
/// Decides which collection a work belongs to under a rule set. Pure and deterministic, like the matcher
/// it builds on: this is the step that decides who may see a title, so it is proved from tables of
/// inputs, and the preview and the sweep run the very same function so a preview cannot promise one
/// thing and a save do another.
/// </summary>
public static class CollectionPlacement
{
    /// <summary>
    /// The first rule that matches claims the work. A work no rule claims goes to the default collection —
    /// even when a rule used to claim it, because leaving it on a shelf whose own rules now exclude it
    /// makes "why is this here" unanswerable. A rule never claims a work its collection's kind cannot hold.
    /// Pins are the caller's concern: a pinned work is not placed at all.
    /// </summary>
    public static Placement Place(RuleWorkFacts work, IReadOnlyList<RankedRule> orderedRules, Guid defaultCollectionId)
    {
        foreach (var rule in orderedRules)
        {
            if (Collection.Holds(rule.CollectionKind, work.Kind)
                && CollectionRuleMatcher.Matches(work, rule.Conditions))
            {
                return new Placement(rule.CollectionId, rule.RuleId);
            }
        }

        return new Placement(defaultCollectionId, null);
    }

    /// <summary>Whether any of <paramref name="rules"/> would claim the work, ignoring what comes before them.</summary>
    public static bool AnyMatches(RuleWorkFacts work, IEnumerable<RankedRule> rules) =>
        rules.Any(rule => Collection.Holds(rule.CollectionKind, work.Kind)
            && CollectionRuleMatcher.Matches(work, rule.Conditions));

    /// <summary>
    /// Flattens collections and their rules into evaluation order: collections by priority, then by id so
    /// a tie still has one answer; rules within a collection by position, then by id.
    /// </summary>
    public static IReadOnlyList<RankedRule> Order(
        IEnumerable<Collection> collections,
        IEnumerable<StoredCollectionRule> rules)
    {
        var byCollection = rules
            .GroupBy(rule => rule.CollectionId)
            .ToDictionary(group => group.Key, group => group.OrderBy(r => r.Position).ThenBy(r => r.Id).ToList());

        return collections
            .OrderBy(c => c.RulePriority)
            .ThenBy(c => c.Id)
            .SelectMany(c => byCollection.TryGetValue(c.Id, out var owned)
                ? owned.Select(r => new RankedRule(r.Id, c.Id, c.Kind, r.Conditions()))
                : [])
            .ToList();
    }

    public static RuleWorkFacts Facts(Work work) => new(
        work.Kind, work.Title, work.Year, work.RuntimeMinutes, work.OriginalLanguage, work.Genres, work.ContentRating);
}
