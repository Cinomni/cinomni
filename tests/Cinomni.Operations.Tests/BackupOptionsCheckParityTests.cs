using Cinomni.Operations.Backup;
using Xunit;

namespace Cinomni.Operations.Tests;

/// <summary>
/// Proves <see cref="BackupOptions.Validate"/> and <see cref="BackupOptions.Check"/> agree for every field
/// <see cref="BackupOptions.Check"/> owns. <see cref="BackupOptionsTests"/> already exercises
/// <see cref="BackupOptions.Validate"/>'s user-visible behaviour in depth (including <c>Root</c>, which
/// stays outside <see cref="BackupOptions.Check"/> — see that method's remarks) and continues to pass
/// unchanged, which is itself evidence the refactor preserved the existing messages exactly.
/// </summary>
public sealed class BackupOptionsCheckParityTests
{
    private static string ValidRoot() => Path.Combine(Path.GetTempPath(), "cinomni-backup-parity-tests");

    public static IEnumerable<object[]> InvalidConfigurations()
    {
        yield return [(Action<BackupOptions>)(o => o.KeepCount = 0), "KeepCount must keep at least one backup"];
        yield return [(Action<BackupOptions>)(o => o.Interval = TimeSpan.Zero), "Backup:Interval must be a positive duration"];
        yield return [(Action<BackupOptions>)(o => o.Timeout = TimeSpan.Zero), "Backup:Timeout must be a positive duration"];
        yield return [(Action<BackupOptions>)(o => o.PgDumpPath = " "), "PgDumpPath must name an executable"];
        yield return [(Action<BackupOptions>)(o => o.PgRestorePath = " "), "PgRestorePath must name an executable"];
    }

    [Theory]
    [MemberData(nameof(InvalidConfigurations))]
    public void Check_and_Validate_agree_on_every_rule_Check_owns(Action<BackupOptions> breakIt, string expectedText)
    {
        var candidate = new BackupOptions { Root = ValidRoot() };
        breakIt(candidate);

        var errors = BackupOptions.Check(candidate).ToList();
        Assert.Contains(errors, e => e.Message.Contains(expectedText, StringComparison.Ordinal));

        var failure = Assert.Throws<InvalidOperationException>(candidate.Validate);
        Assert.Equal(errors[0].Message, failure.Message);
    }

    [Fact]
    public void The_default_configuration_with_a_valid_root_passes_both_shapes()
    {
        var candidate = new BackupOptions { Root = ValidRoot() };

        Assert.Empty(BackupOptions.Check(candidate));
        candidate.Validate(); // must not throw
    }
}
