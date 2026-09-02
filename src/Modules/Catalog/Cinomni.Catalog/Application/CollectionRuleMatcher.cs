using Cinomni.Catalog.Contracts;

namespace Cinomni.Catalog.Application;

/// <summary>
/// The rule-relevant facts of one work, lifted out of the row so matching stays a pure function.
/// <para>
/// Only fields that are stable or change through metadata appear here. Availability, status and
/// episode counts are deliberately absent: a rule over them would move a work between collections as
/// a side effect of a download finishing, and since a collection decides who may see a work, a
/// restricted shelf that opens itself when an episode lands is a security failure shaped like a
/// feature.
/// </para>
/// </summary>
public sealed record RuleWorkFacts(
    WorkKind Kind,
    string Title,
    int? Year,
    int? RuntimeMinutes,
    string? OriginalLanguage,
    IReadOnlyList<string> Genres,
    string? ContentRating);

/// <summary>
/// Decides whether a work satisfies a rule. Pure, deterministic, and free of I/O — this is the piece
/// that decides which collection a work lives in, and therefore who may see it, so it is the piece
/// that has to be provable from a table of inputs rather than inspected in place.
/// <para>
/// A rule is a set of conditions that must <b>all</b> hold; the values inside one condition are
/// alternatives. There is no nesting and no scripting, by the same reasoning the declarative indexer
/// definitions follow: a vocabulary the parser fully understands is one an operator cannot be
/// surprised by.
/// </para>
/// </summary>
public static class CollectionRuleMatcher
{
    /// <summary>Whether every condition holds. An empty set is refused at validation, never matched here.</summary>
    public static bool Matches(RuleWorkFacts work, IReadOnlyList<CollectionRuleCondition> conditions) =>
        conditions.Count > 0 && conditions.All(condition => Matches(work, condition));

    /// <summary>
    /// Whether one condition holds.
    /// <para>
    /// <b>A condition over a field the work has no value for never holds — including a negation.</b>
    /// "Year is not 1999" does not match a work whose year nobody knows, and neither does "year is
    /// 1999". That is the conservative reading and it is chosen deliberately: these rules decide
    /// visibility, so a work should be swept onto a shelf because of what is known about it, never
    /// because of what is missing. A library half-catalogued would otherwise drift into whichever
    /// collection had the first negation.
    /// </para>
    /// </summary>
    public static bool Matches(RuleWorkFacts work, CollectionRuleCondition condition) => condition.Field switch
    {
        CollectionRuleField.Kind => MatchesSet(condition, [work.Kind.ToString()]),
        CollectionRuleField.Genre => MatchesSet(condition, work.Genres),
        CollectionRuleField.ContentRating => MatchesSet(condition, Present(work.ContentRating)),
        CollectionRuleField.OriginalLanguage => MatchesSet(condition, Present(work.OriginalLanguage)),
        CollectionRuleField.Year => MatchesNumber(condition, work.Year),
        CollectionRuleField.RuntimeMinutes => MatchesNumber(condition, work.RuntimeMinutes),
        CollectionRuleField.Title => MatchesText(condition, work.Title),
        _ => false,
    };

    /// <summary>
    /// A set field: <c>Is</c> holds when the work carries any of the values, <c>IsNot</c> when it
    /// carries none of them <em>and</em> carries something. Genre is naturally many-valued and the
    /// others are one value or none, which is why they share this path — a work with no genres and a
    /// work with no rating are the same case.
    /// </summary>
    private static bool MatchesSet(CollectionRuleCondition condition, IReadOnlyList<string> actual)
    {
        if (actual.Count == 0)
        {
            return false;
        }

        var any = actual.Any(value => condition.Values.Contains(value, StringComparer.OrdinalIgnoreCase));
        return condition.Operator switch
        {
            CollectionRuleOperator.Is => any,
            CollectionRuleOperator.IsNot => !any,
            _ => false,
        };
    }

    private static bool MatchesNumber(CollectionRuleCondition condition, int? actual)
    {
        if (actual is not { } value)
        {
            return false;
        }

        var numbers = condition.Values
            .Select(raw => int.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : (int?)null)
            .OfType<int>()
            .ToList();

        if (numbers.Count == 0)
        {
            // Validation refuses an unparseable value on the way in; a stored rule that somehow holds
            // one matches nothing rather than throwing mid-sweep over hundreds of works.
            return false;
        }

        return condition.Operator switch
        {
            CollectionRuleOperator.Is => numbers.Contains(value),
            CollectionRuleOperator.IsNot => !numbers.Contains(value),
            CollectionRuleOperator.AtLeast => value >= numbers[0],
            CollectionRuleOperator.AtMost => value <= numbers[0],
            _ => false,
        };
    }

    /// <summary>
    /// Text, matched case-insensitively and literally. No regular expressions, ever: a pattern an
    /// operator cannot reason about deciding who sees a title is not a feature, and a catastrophic
    /// one evaluated across a whole library is a way to stop the sweep entirely.
    /// </summary>
    private static bool MatchesText(CollectionRuleCondition condition, string actual)
    {
        var needle = condition.Values.Count > 0 ? condition.Values[0] : null;
        if (string.IsNullOrEmpty(needle))
        {
            return false;
        }

        return condition.Operator switch
        {
            CollectionRuleOperator.Contains => actual.Contains(needle, StringComparison.OrdinalIgnoreCase),
            CollectionRuleOperator.StartsWith => actual.StartsWith(needle, StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    private static IReadOnlyList<string> Present(string? value) =>
        string.IsNullOrWhiteSpace(value) ? [] : [value];
}
