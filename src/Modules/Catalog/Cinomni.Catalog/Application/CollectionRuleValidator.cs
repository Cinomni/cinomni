using System.Globalization;
using Cinomni.Catalog.Contracts;
using Cinomni.Kernel.Results;

namespace Cinomni.Catalog.Application;

/// <summary>
/// Refuses a rule the engine could not honour, at the moment somebody writes it rather than months
/// later when a sweep quietly matches nothing.
/// <para>
/// The field/operator table lives here and nowhere else. It is the same table the rule builder in the
/// web client offers, so a client that stays in step never proposes something this rejects — and a
/// client that drifts is refused rather than silently producing a rule that matches no work at all.
/// </para>
/// </summary>
public static class CollectionRuleValidator
{
    /// <summary>A rule nobody could reason about is not a rule. Bounded so one cannot become a denial of service.</summary>
    private const int MaxConditions = 20;

    private const int MaxValuesPerCondition = 50;

    private const int MaxValueLength = 200;

    /// <summary>Which operators each field accepts. Anything outside this is a refusal, not a fallback.</summary>
    private static readonly Dictionary<CollectionRuleField, CollectionRuleOperator[]> Allowed = new()
    {
        [CollectionRuleField.Kind] = [CollectionRuleOperator.Is, CollectionRuleOperator.IsNot],
        [CollectionRuleField.Genre] = [CollectionRuleOperator.Is, CollectionRuleOperator.IsNot],
        [CollectionRuleField.ContentRating] = [CollectionRuleOperator.Is, CollectionRuleOperator.IsNot],
        [CollectionRuleField.OriginalLanguage] = [CollectionRuleOperator.Is, CollectionRuleOperator.IsNot],
        [CollectionRuleField.Year] =
            [CollectionRuleOperator.Is, CollectionRuleOperator.IsNot, CollectionRuleOperator.AtLeast, CollectionRuleOperator.AtMost],
        [CollectionRuleField.RuntimeMinutes] = [CollectionRuleOperator.AtLeast, CollectionRuleOperator.AtMost],
        [CollectionRuleField.Title] = [CollectionRuleOperator.Contains, CollectionRuleOperator.StartsWith],
    };

    /// <summary>Operators that compare against exactly one value; the rest take a list of alternatives.</summary>
    private static readonly CollectionRuleOperator[] SingleValued =
        [CollectionRuleOperator.AtLeast, CollectionRuleOperator.AtMost,
         CollectionRuleOperator.Contains, CollectionRuleOperator.StartsWith];

    private static readonly CollectionRuleField[] Numeric =
        [CollectionRuleField.Year, CollectionRuleField.RuntimeMinutes];

    public static Result Validate(IReadOnlyList<CollectionRuleCondition> conditions)
    {
        if (conditions.Count == 0)
        {
            // A rule with no conditions matches every work, which would sweep the whole library onto
            // one shelf — and since a shelf decides who may see a work, that is not a harmless empty
            // form. It is refused rather than treated as "no filter".
            return Result.Failure(new Error(
                "catalog.rule.no_conditions", "A rule needs at least one condition; an empty rule would match everything."));
        }

        if (conditions.Count > MaxConditions)
        {
            return Result.Failure(new Error(
                "catalog.rule.too_many_conditions", $"A rule may have at most {MaxConditions} conditions."));
        }

        foreach (var condition in conditions)
        {
            var checkedCondition = ValidateCondition(condition);
            if (checkedCondition.IsFailure)
            {
                return checkedCondition;
            }
        }

        return Result.Success();
    }

    private static Result ValidateCondition(CollectionRuleCondition condition)
    {
        if (!Allowed.TryGetValue(condition.Field, out var operators))
        {
            return Result.Failure(new Error(
                "catalog.rule.unknown_field", $"'{condition.Field}' is not a field a rule can match on."));
        }

        if (!operators.Contains(condition.Operator))
        {
            return Result.Failure(new Error(
                "catalog.rule.operator_not_allowed",
                $"'{condition.Operator}' cannot be used with '{condition.Field}'."));
        }

        if (condition.Values.Count == 0)
        {
            return Result.Failure(new Error(
                "catalog.rule.invalid_value", $"'{condition.Field}' needs a value to compare against."));
        }

        if (SingleValued.Contains(condition.Operator) && condition.Values.Count > 1)
        {
            return Result.Failure(new Error(
                "catalog.rule.too_many_values", $"'{condition.Operator}' compares against exactly one value."));
        }

        if (condition.Values.Count > MaxValuesPerCondition)
        {
            return Result.Failure(new Error(
                "catalog.rule.too_many_values", $"A condition may list at most {MaxValuesPerCondition} values."));
        }

        foreach (var value in condition.Values)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > MaxValueLength)
            {
                return Result.Failure(new Error(
                    "catalog.rule.invalid_value", $"'{condition.Field}' has a value that is empty or too long."));
            }

            // Numbers are refused here rather than at match time, where an unparseable value would
            // simply match nothing and look like a rule that does not work for no visible reason.
            if (Numeric.Contains(condition.Field)
                && !int.TryParse(value, CultureInfo.InvariantCulture, out _))
            {
                return Result.Failure(new Error(
                    "catalog.rule.invalid_value", $"'{value}' is not a number, which '{condition.Field}' compares."));
            }

            if (condition.Field == CollectionRuleField.Kind && !Enum.TryParse<WorkKind>(value, out _))
            {
                return Result.Failure(new Error(
                    "catalog.rule.invalid_value", $"'{value}' is not a kind of work."));
            }
        }

        return Result.Success();
    }
}
