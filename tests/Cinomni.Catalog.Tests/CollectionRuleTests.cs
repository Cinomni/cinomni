using Cinomni.Catalog.Application;
using Cinomni.Catalog.Contracts;

namespace Cinomni.Catalog.Tests;

/// <summary>
/// The rule engine, as a table of inputs. No database and no HTTP: this is the piece that decides
/// which collection a work lives in, and a collection decides who may see it, so it is the piece that
/// has to be provable rather than inspected.
/// </summary>
public sealed class CollectionRuleTests
{
    private static RuleWorkFacts Movie(
        string title = "The Thing",
        int? year = 1982,
        int? runtime = 109,
        string? language = "en",
        string[]? genres = null,
        string? rating = "R") =>
        new(WorkKind.Movie, title, year, runtime, language, genres ?? ["Horror", "Science Fiction"], rating);

    private static CollectionRuleCondition Condition(
        CollectionRuleField field, CollectionRuleOperator op, params string[] values) =>
        new(field, op, values);

    [Fact]
    public void Every_condition_has_to_hold()
    {
        var conditions = new[]
        {
            Condition(CollectionRuleField.Kind, CollectionRuleOperator.Is, "Movie"),
            Condition(CollectionRuleField.Genre, CollectionRuleOperator.Is, "Horror"),
        };

        Assert.True(CollectionRuleMatcher.Matches(Movie(), conditions));
        // One failing condition fails the rule: conditions are an AND, and anyone wanting an OR
        // across fields writes a second rule rather than getting one by accident.
        Assert.False(CollectionRuleMatcher.Matches(Movie(genres: ["Comedy"]), conditions));
    }

    [Fact]
    public void Values_inside_one_condition_are_alternatives()
    {
        var horrorOrThriller = Condition(CollectionRuleField.Genre, CollectionRuleOperator.Is, "Thriller", "Horror");

        Assert.True(CollectionRuleMatcher.Matches(Movie(), horrorOrThriller));
        Assert.False(CollectionRuleMatcher.Matches(Movie(genres: ["Comedy"]), horrorOrThriller));
    }

    [Fact]
    public void Genre_matches_when_any_of_the_works_genres_qualifies()
    {
        // A work carries several; the condition holds if one of them does. IsNot is the strict
        // reverse: not one of the work's genres may be in the list.
        Assert.True(CollectionRuleMatcher.Matches(
            Movie(genres: ["Drama", "Horror"]), Condition(CollectionRuleField.Genre, CollectionRuleOperator.Is, "Horror")));
        Assert.False(CollectionRuleMatcher.Matches(
            Movie(genres: ["Drama", "Horror"]), Condition(CollectionRuleField.Genre, CollectionRuleOperator.IsNot, "Horror")));
        Assert.True(CollectionRuleMatcher.Matches(
            Movie(genres: ["Drama"]), Condition(CollectionRuleField.Genre, CollectionRuleOperator.IsNot, "Horror")));
    }

    [Theory]
    [InlineData(CollectionRuleOperator.Is, "1982", true)]
    [InlineData(CollectionRuleOperator.Is, "1999", false)]
    [InlineData(CollectionRuleOperator.IsNot, "1999", true)]
    [InlineData(CollectionRuleOperator.AtLeast, "1980", true)]
    [InlineData(CollectionRuleOperator.AtLeast, "1990", false)]
    [InlineData(CollectionRuleOperator.AtMost, "1990", true)]
    [InlineData(CollectionRuleOperator.AtMost, "1980", false)]
    public void Numbers_compare_as_numbers(CollectionRuleOperator op, string value, bool expected)
    {
        Assert.Equal(expected, CollectionRuleMatcher.Matches(
            Movie(), Condition(CollectionRuleField.Year, op, value)));
    }

    [Fact]
    public void Text_matches_literally_and_ignores_case()
    {
        Assert.True(CollectionRuleMatcher.Matches(
            Movie(), Condition(CollectionRuleField.Title, CollectionRuleOperator.Contains, "thing")));
        Assert.True(CollectionRuleMatcher.Matches(
            Movie(), Condition(CollectionRuleField.Title, CollectionRuleOperator.StartsWith, "the ")));
        // No pattern language, ever: a title condition is a substring, so an expression that reads
        // like a wildcard is compared as the characters it is made of.
        Assert.False(CollectionRuleMatcher.Matches(
            Movie(), Condition(CollectionRuleField.Title, CollectionRuleOperator.Contains, "Th.ng")));
    }

    [Theory]
    [InlineData(CollectionRuleOperator.Is)]
    [InlineData(CollectionRuleOperator.IsNot)]
    [InlineData(CollectionRuleOperator.AtLeast)]
    [InlineData(CollectionRuleOperator.AtMost)]
    public void A_field_the_work_knows_nothing_about_never_matches(CollectionRuleOperator op)
    {
        // Including the negation, which is the surprising half and the deliberate one. A rule decides
        // who may see a work, so a work should land on a shelf because of what is known about it and
        // never because of what is missing — otherwise a half-catalogued library drifts into whichever
        // collection happened to carry the first "is not".
        Assert.False(CollectionRuleMatcher.Matches(
            Movie(year: null), Condition(CollectionRuleField.Year, op, "1999")));
    }

    [Fact]
    public void A_work_with_no_genres_or_no_rating_matches_neither_direction()
    {
        Assert.False(CollectionRuleMatcher.Matches(
            Movie(genres: []), Condition(CollectionRuleField.Genre, CollectionRuleOperator.IsNot, "Horror")));
        Assert.False(CollectionRuleMatcher.Matches(
            Movie(rating: null), Condition(CollectionRuleField.ContentRating, CollectionRuleOperator.IsNot, "R")));
    }

    [Fact]
    public void An_empty_rule_matches_nothing_even_if_one_reached_the_matcher()
    {
        // Validation refuses it; the matcher refuses it again, because "matches everything" is the
        // one wrong answer that would move an entire library onto a single shelf.
        Assert.False(CollectionRuleMatcher.Matches(Movie(), []));
    }

    // -- validation ------------------------------------------------------------------------------

    [Fact]
    public void A_rule_with_no_conditions_is_refused()
    {
        var result = CollectionRuleValidator.Validate([]);

        Assert.True(result.IsFailure);
        Assert.Equal("catalog.rule.no_conditions", result.Error.Code);
    }

    [Theory]
    [InlineData(CollectionRuleField.Genre, CollectionRuleOperator.AtLeast)]
    [InlineData(CollectionRuleField.Title, CollectionRuleOperator.Is)]
    [InlineData(CollectionRuleField.RuntimeMinutes, CollectionRuleOperator.Contains)]
    public void An_operator_a_field_does_not_take_is_refused(CollectionRuleField field, CollectionRuleOperator op)
    {
        var result = CollectionRuleValidator.Validate([Condition(field, op, "x")]);

        Assert.True(result.IsFailure);
        Assert.Equal("catalog.rule.operator_not_allowed", result.Error.Code);
    }

    [Fact]
    public void A_value_that_is_not_a_number_is_refused_where_a_number_is_compared()
    {
        // Refused on the way in rather than at match time, where it would match nothing and look like
        // a rule that mysteriously does not work.
        var result = CollectionRuleValidator.Validate(
            [Condition(CollectionRuleField.Year, CollectionRuleOperator.AtLeast, "nineteen ninety")]);

        Assert.True(result.IsFailure);
        Assert.Equal("catalog.rule.invalid_value", result.Error.Code);
    }

    [Fact]
    public void An_operator_that_compares_one_value_is_refused_a_list()
    {
        var result = CollectionRuleValidator.Validate(
            [Condition(CollectionRuleField.Year, CollectionRuleOperator.AtLeast, "1980", "1990")]);

        Assert.True(result.IsFailure);
        Assert.Equal("catalog.rule.too_many_values", result.Error.Code);
    }

    [Fact]
    public void A_kind_that_is_not_a_kind_of_work_is_refused()
    {
        var result = CollectionRuleValidator.Validate(
            [Condition(CollectionRuleField.Kind, CollectionRuleOperator.Is, "Documentary")]);

        Assert.True(result.IsFailure);
        Assert.Equal("catalog.rule.invalid_value", result.Error.Code);
    }

    [Fact]
    public void A_rule_the_builder_would_offer_is_accepted()
    {
        var result = CollectionRuleValidator.Validate(
        [
            Condition(CollectionRuleField.Kind, CollectionRuleOperator.Is, "Movie"),
            Condition(CollectionRuleField.Genre, CollectionRuleOperator.Is, "Horror", "Thriller"),
            Condition(CollectionRuleField.ContentRating, CollectionRuleOperator.Is, "R", "NC-17"),
            Condition(CollectionRuleField.Year, CollectionRuleOperator.AtLeast, "1980"),
        ]);

        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Message);
    }
}
