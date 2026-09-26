using Cinomni.Decision.Contracts;
using Cinomni.Decision.Evaluation;
using Cinomni.Decision.Persistence;
using Cinomni.Kernel.Identifiers;
using static Cinomni.Decision.Tests.DecisionFixtures;

namespace Cinomni.Decision.Tests;

/// <summary>Unit tests for custom-format matching: OR within a type, AND across types, negate, required.</summary>
public sealed class FormatMatcherTests
{
    [Fact]
    public void Matches_or_within_a_type_and_and_across_types()
    {
        // (Source == Bluray OR WebDl) AND (Resolution == 1080p)
        var rule = Rule(
            (FormatConditionType.Source, "Bluray", false, false),
            (FormatConditionType.Source, "WebDl", false, false),
            (FormatConditionType.Resolution, "1080p", false, false));

        Assert.True(FormatMatcher.Matches(rule, Parse("Movie.2020.1080p.WEB-DL-GRP"), Candidate("g", "m")));
        Assert.False(FormatMatcher.Matches(rule, Parse("Movie.2020.720p.WEB-DL-GRP"), Candidate("g", "m")));
        Assert.False(FormatMatcher.Matches(rule, Parse("Movie.2020.1080p.HDTV-GRP"), Candidate("g", "m")));
    }

    [Fact]
    public void A_negated_condition_inverts_the_test()
    {
        // Source != Bluray
        var rule = Rule((FormatConditionType.Source, "Bluray", true, false));

        Assert.False(FormatMatcher.Matches(rule, Parse("Movie.2020.1080p.BluRay-GRP"), Candidate("g", "m")));
        Assert.True(FormatMatcher.Matches(rule, Parse("Movie.2020.1080p.WEB-DL-GRP"), Candidate("g", "m")));
    }

    [Fact]
    public void A_required_condition_that_fails_kills_the_group()
    {
        // Within the Source group: Bluray is required, WebDl is optional. A WebDl release fails
        // the required Bluray, so the group (and the rule) does not match despite WebDl being present.
        var rule = Rule(
            (FormatConditionType.Source, "Bluray", false, true),
            (FormatConditionType.Source, "WebDl", false, false));

        Assert.True(FormatMatcher.Matches(rule, Parse("Movie.2020.1080p.BluRay-GRP"), Candidate("g", "m")));
        Assert.False(FormatMatcher.Matches(rule, Parse("Movie.2020.1080p.WEB-DL-GRP"), Candidate("g", "m")));
    }

    private static FormatRule Rule(params (FormatConditionType Type, string Value, bool Negate, bool Required)[] conditions)
    {
        var rule = new FormatRule { Id = Uuid7.New(), ProfileId = Uuid7.New(), Name = "R", Score = 1 };
        foreach (var (type, value, negate, required) in conditions)
        {
            rule.Conditions.Add(new FormatCondition
            {
                Id = Uuid7.New(),
                RuleId = rule.Id,
                Type = type,
                Value = value,
                Negate = negate,
                Required = required,
            });
        }

        return rule;
    }
}
