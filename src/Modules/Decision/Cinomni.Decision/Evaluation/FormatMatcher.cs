using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Cinomni.Decision.Contracts;
using Cinomni.Decision.Persistence;
using Cinomni.Discovery.Contracts;
using Cinomni.ReleaseParsing.Contracts;

namespace Cinomni.Decision.Evaluation;

/// <summary>
/// Decides whether a custom format (a <see cref="FormatRule"/>) matches a parsed release. Semantics:
/// a condition is <c>raw XOR negate</c>; a type group matches iff no
/// Required condition fails and at least one condition is true; the rule matches iff every type
/// group matches (OR within a type, AND across types), then <see cref="FormatRule.Negate"/> inverts.
/// </summary>
internal static class FormatMatcher
{
    private static readonly ConcurrentDictionary<string, Regex?> RegexCache = new();

    public static bool Matches(FormatRule rule, ParsedRelease parsed, ReleaseCandidate candidate)
    {
        if (rule.Conditions.Count == 0)
        {
            return rule.Negate;
        }

        var allGroupsMatch = rule.Conditions
            .GroupBy(c => c.Type)
            .All(group => GroupMatches(group, parsed, candidate));

        return allGroupsMatch ^ rule.Negate;
    }

    private static bool GroupMatches(IEnumerable<FormatCondition> group, ParsedRelease parsed, ReleaseCandidate candidate)
    {
        var anyTrue = false;
        foreach (var condition in group)
        {
            var effective = ConditionMatches(condition, parsed, candidate) ^ condition.Negate;
            if (condition.Required && !effective)
            {
                return false;
            }

            anyTrue |= effective;
        }

        return anyTrue;
    }

    private static bool ConditionMatches(FormatCondition condition, ParsedRelease parsed, ReleaseCandidate candidate) =>
        condition.Type switch
        {
            FormatConditionType.ReleaseTitle => TitleMatches(condition.Value, parsed.SourceTitle),
            FormatConditionType.Source => Equals(parsed.Quality.Source.ToString(), condition.Value),
            FormatConditionType.Resolution => ResolutionMatches(condition.Value, parsed.Quality.Resolution),
            FormatConditionType.Language => parsed.Languages.Any(l => Equals(l, condition.Value)),
            FormatConditionType.ReleaseGroup => parsed.ReleaseGroup is not null && Equals(parsed.ReleaseGroup, condition.Value),
            FormatConditionType.Edition => parsed.Edition is not null
                && parsed.Edition.Contains(condition.Value, StringComparison.OrdinalIgnoreCase),
            FormatConditionType.Year => parsed.Year is int y
                && condition.Value == y.ToString(CultureInfo.InvariantCulture),
            FormatConditionType.QualityModifier => Equals(parsed.Quality.Modifier.ToString(), condition.Value),
            FormatConditionType.ReleaseType => Equals(parsed.ReleaseType.ToString(), condition.Value),
            FormatConditionType.Size => SizeMatches(condition.Value, candidate.SizeBytes),
            FormatConditionType.IndexerFlag => false, // indexer flags are not modelled in the MVP
            _ => false,
        };

    private static bool Equals(string actual, string expected) =>
        string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

    private static bool ResolutionMatches(string value, QualityResolution resolution)
    {
        var digits = new string(value.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var height)
            && height == (int)resolution;
    }

    private static bool SizeMatches(string value, long bytes)
    {
        var parts = value.Split('-', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2
            && long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var min)
            && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var max)
            && bytes >= min && bytes <= max;
    }

    private static bool TitleMatches(string pattern, string title)
    {
        var regex = RegexCache.GetOrAdd(pattern, static p =>
        {
            try
            {
                return new Regex(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
            }
            catch (ArgumentException)
            {
                return null; // an admin-entered invalid pattern never matches (and never throws at eval time)
            }
        });

        if (regex is null)
        {
            return false;
        }

        try
        {
            return regex.IsMatch(title);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
