using Cinomni.Catalog.Application;
using Cinomni.Catalog.Contracts;
using Cinomni.Catalog.Persistence;
using Xunit;

namespace Cinomni.Catalog.Tests;

/// <summary>
/// The placement decision as a table of inputs: which rule claims a work, what happens when none does,
/// and what the evaluation order is. The preview and the sweep both run exactly this function.
/// </summary>
public sealed class CollectionPlacementTests
{
    private static readonly Guid Default = Guid.Parse("01000000-0000-7000-8000-000000000001");
    private static readonly Guid Horror = Guid.Parse("01000000-0000-7000-8000-0000000000a1");
    private static readonly Guid Kids = Guid.Parse("01000000-0000-7000-8000-0000000000a2");
    private static readonly Guid ShowsOnly = Guid.Parse("01000000-0000-7000-8000-0000000000a3");

    private static readonly CollectionRuleCondition IsHorror =
        new(CollectionRuleField.Genre, CollectionRuleOperator.Is, ["Horror"]);

    private static readonly CollectionRuleCondition IsFamily =
        new(CollectionRuleField.Genre, CollectionRuleOperator.Is, ["Family"]);

    private static RuleWorkFacts Movie(params string[] genres) =>
        new(WorkKind.Movie, "Some Title", 2001, 100, "en", genres, null);

    private static RankedRule Rule(Guid collection, CollectionRuleCondition condition, CollectionKind kind = CollectionKind.Mixed) =>
        new(Guid.NewGuid(), collection, kind, [condition]);

    [Fact]
    public void The_first_rule_that_matches_claims_the_work()
    {
        var horror = Rule(Horror, IsHorror);
        var kids = Rule(Kids, IsFamily);

        var placement = CollectionPlacement.Place(Movie("Family", "Horror"), [kids, horror], Default);

        Assert.Equal(new Placement(Kids, kids.RuleId), placement);
    }

    [Fact]
    public void A_work_no_rule_claims_goes_to_the_default_collection_with_no_rule()
    {
        var placement = CollectionPlacement.Place(Movie("Drama"), [Rule(Horror, IsHorror)], Default);

        Assert.Equal(new Placement(Default, null), placement);
    }

    [Fact]
    public void A_rule_never_claims_a_work_its_collection_cannot_hold()
    {
        // A series-only shelf with a rule that would otherwise match a film.
        var shows = Rule(ShowsOnly, IsHorror, CollectionKind.Series);
        var horror = Rule(Horror, IsHorror);

        var placement = CollectionPlacement.Place(Movie("Horror"), [shows, horror], Default);

        Assert.Equal(Horror, placement.CollectionId);
    }

    [Fact]
    public void A_work_with_no_genres_yet_is_not_claimed_by_a_genre_rule_even_a_negated_one()
    {
        // Before enrichment a work has no genres; "is not Family" must not sweep it anywhere.
        var notFamily = Rule(Horror, new CollectionRuleCondition(CollectionRuleField.Genre, CollectionRuleOperator.IsNot, ["Family"]));

        var placement = CollectionPlacement.Place(Movie(), [notFamily], Default);

        Assert.Equal(Default, placement.CollectionId);
    }

    [Fact]
    public void Collections_are_ordered_by_priority_then_id_and_rules_by_position_then_id()
    {
        var now = DateTimeOffset.UtcNow;
        var late = Collection.Create("Late", CollectionKind.Mixed, CollectionAccessMode.Open, rulePriority: 1, now);
        var early = Collection.Create("Early", CollectionKind.Mixed, CollectionAccessMode.Open, rulePriority: 0, now);
        var lateRule = StoredCollectionRule.Create(late.Id, "late", 0, [IsHorror], now);
        var earlySecond = StoredCollectionRule.Create(early.Id, "second", 1, [IsHorror], now);
        var earlyFirst = StoredCollectionRule.Create(early.Id, "first", 0, [IsFamily], now);

        var ordered = CollectionPlacement.Order([late, early], [lateRule, earlySecond, earlyFirst]);

        Assert.Equal([earlyFirst.Id, earlySecond.Id, lateRule.Id], ordered.Select(r => r.RuleId));
        var condition = Assert.Single(ordered[0].Conditions);
        Assert.Equal((IsFamily.Field, IsFamily.Operator), (condition.Field, condition.Operator));
        Assert.Equal(IsFamily.Values, condition.Values);
    }

    [Fact]
    public void A_stored_rule_whose_document_no_longer_reads_claims_nothing()
    {
        var rule = StoredCollectionRule.Create(Horror, "damaged", 0, [IsHorror], DateTimeOffset.UtcNow);
        rule.ConditionsJson = "{not json";

        Assert.Empty(rule.Conditions());
        Assert.False(CollectionRuleMatcher.Matches(Movie("Horror"), rule.Conditions()));
    }
}
