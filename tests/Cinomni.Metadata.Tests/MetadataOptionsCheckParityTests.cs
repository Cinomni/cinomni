using Cinomni.Metadata.Application;
using Xunit;

namespace Cinomni.Metadata.Tests;

/// <summary>
/// Proves <see cref="MetadataOptions.Validate"/> and <see cref="MetadataOptions.Check"/> can never drift —
/// one implementation, two shapes, same rule as every other options class the settings store's write path
/// will eventually validate through <see cref="MetadataOptions.Check"/>.
/// </summary>
public sealed class MetadataOptionsCheckParityTests
{
    public static IEnumerable<object[]> InvalidConfigurations()
    {
        yield return
        [
            (Action<MetadataOptions>)(o => o.SnapshotRetention = TimeSpan.Zero),
            "Retention:Metadata:SnapshotRetention must be a positive duration",
        ];
        yield return
        [
            (Action<MetadataOptions>)(o => o.SnapshotPurgeInterval = TimeSpan.Zero),
            "Retention:Metadata:Interval must be a positive duration",
        ];
    }

    [Theory]
    [MemberData(nameof(InvalidConfigurations))]
    public void Check_and_Validate_agree_on_every_rule(Action<MetadataOptions> breakIt, string expectedText)
    {
        var candidate = new MetadataOptions();
        breakIt(candidate);

        var errors = MetadataOptions.Check(candidate).ToList();
        Assert.Contains(errors, e => e.Message.Contains(expectedText, StringComparison.Ordinal));

        var failure = Assert.Throws<InvalidOperationException>(candidate.Validate);
        Assert.Equal(errors[0].Message, failure.Message);
    }

    [Fact]
    public void The_default_configuration_passes_both_shapes()
    {
        var candidate = new MetadataOptions();

        Assert.Empty(MetadataOptions.Check(candidate));
        candidate.Validate(); // must not throw
    }
}
