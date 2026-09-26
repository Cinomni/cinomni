using Cinomni.Decision.Application;
using Xunit;

namespace Cinomni.Decision.Tests;

/// <summary>
/// Proves <see cref="DecisionRetentionOptions.Validate"/> and <see cref="DecisionRetentionOptions.Check"/>
/// can never drift — one implementation, two shapes, same rule as every other options class the settings
/// store's write path will eventually validate through <see cref="DecisionRetentionOptions.Check"/>.
/// </summary>
public sealed class DecisionRetentionOptionsCheckParityTests
{
    public static IEnumerable<object[]> InvalidConfigurations()
    {
        yield return
        [
            (Action<DecisionRetentionOptions>)(o => o.EvaluationRetention = TimeSpan.Zero),
            "Retention:Decision:EvaluationRetention must be a positive duration",
        ];
        yield return
        [
            (Action<DecisionRetentionOptions>)(o => o.Interval = TimeSpan.Zero),
            "Retention:Decision:Interval must be a positive duration",
        ];
    }

    [Theory]
    [MemberData(nameof(InvalidConfigurations))]
    public void Check_and_Validate_agree_on_every_rule(Action<DecisionRetentionOptions> breakIt, string expectedText)
    {
        var candidate = new DecisionRetentionOptions();
        breakIt(candidate);

        var errors = DecisionRetentionOptions.Check(candidate).ToList();
        Assert.Contains(errors, e => e.Message.Contains(expectedText, StringComparison.Ordinal));

        var failure = Assert.Throws<InvalidOperationException>(candidate.Validate);
        Assert.Equal(errors[0].Message, failure.Message);
    }

    [Fact]
    public void The_default_configuration_passes_both_shapes()
    {
        var candidate = new DecisionRetentionOptions();

        Assert.Empty(DecisionRetentionOptions.Check(candidate));
        candidate.Validate(); // must not throw
    }
}
