using Cinomni.Operations.Retention;
using Xunit;

namespace Cinomni.Operations.Tests;

/// <summary>
/// Proves <see cref="RetentionOptions.Validate"/> and <see cref="RetentionOptions.Check"/> can never
/// drift: one implementation (<see cref="RetentionOptions.Check"/>), two shapes. If they ever disagreed, a
/// value the settings panel accepted could stop the next startup — the worst outcome the store can
/// produce.
/// </summary>
public sealed class RetentionOptionsCheckParityTests
{
    public static IEnumerable<object[]> InvalidConfigurations()
    {
        yield return
        [
            (Action<RetentionOptions>)(o => o.OutboxRetention = TimeSpan.Zero),
            "OutboxRetention must be a positive duration",
        ];
        yield return
        [
            (Action<RetentionOptions>)(o => o.CompletedCommandRetention = TimeSpan.Zero),
            "CompletedCommandRetention must be a positive duration",
        ];
        yield return
        [
            (Action<RetentionOptions>)(o => o.FailedCommandRetention = TimeSpan.Zero),
            "FailedCommandRetention must be a positive duration",
        ];
        yield return
        [
            (Action<RetentionOptions>)(o => o.Interval = TimeSpan.Zero),
            "Interval must be a positive duration",
        ];
        yield return
        [
            (Action<RetentionOptions>)(o => o.BatchSize = 0),
            "BatchSize must be greater than zero",
        ];
    }

    [Theory]
    [MemberData(nameof(InvalidConfigurations))]
    public void Check_and_Validate_agree_on_every_single_key_rule(Action<RetentionOptions> breakIt, string expectedText)
    {
        var candidate = new RetentionOptions();
        breakIt(candidate);

        // Breaking one field can also trip the cross-key rule below it (e.g. zeroing
        // CompletedCommandRetention is both "not positive" and "not greater than OutboxRetention") —
        // Check() collects every violated rule, exactly what the settings panel needs. Validate() still
        // throws on only the first, in the same order Check() yields them, so parity is asserted on that
        // first message rather than assuming there is exactly one.
        var errors = RetentionOptions.Check(candidate).ToList();
        Assert.Contains(errors, e => e.Message.Contains(expectedText, StringComparison.Ordinal));

        var failure = Assert.Throws<InvalidOperationException>(candidate.Validate);
        Assert.Equal(errors[0].Message, failure.Message);
    }

    [Fact]
    public void Check_and_Validate_agree_a_completed_window_inside_the_outbox_window_is_rejected()
    {
        var candidate = new RetentionOptions { OutboxRetention = TimeSpan.FromDays(60), CompletedCommandRetention = TimeSpan.FromDays(30) };

        var error = Assert.Single(RetentionOptions.Check(candidate));
        Assert.Equal(["retention.operations.completedCommandRetention", "retention.operations.outboxRetention"], error.Keys);

        var failure = Assert.Throws<InvalidOperationException>(candidate.Validate);
        Assert.Equal(error.Message, failure.Message);
    }

    [Fact]
    public void Check_and_Validate_agree_a_failed_window_inside_the_outbox_window_is_rejected()
    {
        // CompletedCommandRetention is left at its default (30 days), comfortably above OutboxRetention,
        // so only the failed-vs-outbox rule fires — isolating that one rule from its sibling.
        var candidate = new RetentionOptions { OutboxRetention = TimeSpan.FromDays(20), FailedCommandRetention = TimeSpan.FromDays(10) };

        var error = Assert.Single(RetentionOptions.Check(candidate));

        var failure = Assert.Throws<InvalidOperationException>(candidate.Validate);
        Assert.Equal(error.Message, failure.Message);
    }

    [Fact]
    public void The_default_configuration_passes_both_shapes()
    {
        var candidate = new RetentionOptions();

        Assert.Empty(RetentionOptions.Check(candidate));
        candidate.Validate(); // must not throw
    }
}
