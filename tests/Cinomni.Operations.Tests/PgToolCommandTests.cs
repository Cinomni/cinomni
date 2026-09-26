using Cinomni.Operations.Backup;
using Xunit;

namespace Cinomni.Operations.Tests;

/// <summary>
/// The process boundary of the backup feature, asserted without launching anything.
/// <para>
/// Two properties matter and neither is visible by reading a happy-path test: every argument is a
/// separate <c>ArgumentList</c> entry (so a database name or a path containing shell metacharacters is
/// inert), and the password reaches the child through its environment rather than through argv — an
/// argument vector is world-readable through <c>/proc</c> on the node this runs on.
/// </para>
/// </summary>
public sealed class PgToolCommandTests
{
    private const string ConnectionString =
        "Host=db.internal;Port=5442;Database=cinomni;Username=cinomni;Password=s3cr3t-not-in-argv";

    private static PgConnectionTarget Target() => PgToolCommand.TargetFrom(ConnectionString);

    [Fact]
    public void The_connection_target_is_read_out_of_the_connection_string()
    {
        var target = Target();

        Assert.Equal("db.internal", target.Host);
        Assert.Equal(5442, target.Port);
        Assert.Equal("cinomni", target.Username);
        Assert.Equal("cinomni", target.Database);
        Assert.Equal("s3cr3t-not-in-argv", target.Password);
    }

    /// <summary>
    /// A record would print every property from its generated <c>ToString</c>, and one interpolation in
    /// a log message would then publish the database password.
    /// </summary>
    [Fact]
    public void Describing_the_target_never_includes_the_password()
    {
        var description = Target().ToString();

        Assert.DoesNotContain("s3cr3t-not-in-argv", description, StringComparison.Ordinal);
        Assert.Equal("cinomni@db.internal:5442/cinomni", description);
    }

    [Fact]
    public void The_dump_command_passes_every_argument_separately_and_never_uses_a_shell()
    {
        var startInfo = PgToolCommand.Dump("pg_dump", Target(), "/data/backups/cinomni.dump.partial");

        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.Equal("pg_dump", startInfo.FileName);

        // A concatenated command line is what argument injection needs; there must not be one.
        Assert.Equal(string.Empty, startInfo.Arguments);

        Assert.Contains("--format=custom", startInfo.ArgumentList);
        Assert.Contains("/data/backups/cinomni.dump.partial", startInfo.ArgumentList);
        Assert.Contains("db.internal", startInfo.ArgumentList);
        Assert.Contains("5442", startInfo.ArgumentList);

        // Every entry is one argument: no entry is a joined "--host db.internal" pair.
        Assert.All(startInfo.ArgumentList, argument => Assert.DoesNotContain(' ', argument));
    }

    [Fact]
    public void The_password_travels_in_the_environment_and_appears_nowhere_in_the_argument_vector()
    {
        var startInfo = PgToolCommand.Dump("pg_dump", Target(), "/data/backups/cinomni.dump.partial");

        Assert.Equal("s3cr3t-not-in-argv", startInfo.Environment[PgToolCommand.PasswordVariable]);
        Assert.DoesNotContain("s3cr3t-not-in-argv", startInfo.ArgumentList);
        Assert.DoesNotContain("s3cr3t-not-in-argv", startInfo.Arguments, StringComparison.Ordinal);
    }

    /// <summary>
    /// Without this, a missing or wrong credential makes libpq prompt on a terminal that is not there,
    /// and a scheduled backup hangs until its timeout instead of failing in the first second.
    /// </summary>
    [Fact]
    public void Both_tools_refuse_to_prompt_for_a_password()
    {
        Assert.Contains("--no-password", PgToolCommand.Dump("pg_dump", Target(), "out.dump").ArgumentList);
        Assert.Contains(
            "--no-password",
            PgToolCommand.Restore("pg_restore", Target(), "cinomni_restored", "in.dump").ArgumentList);
    }

    /// <summary>
    /// A restore either lands completely or leaves the database as it was. Ownership and privileges are
    /// dropped because the restoring role need not be the dumping one on a self-hosted node.
    /// </summary>
    [Fact]
    public void The_restore_command_is_atomic_and_lands_in_one_transaction()
    {
        var startInfo = PgToolCommand.Restore("pg_restore", Target(), "cinomni_restored", "/backups/in.dump");

        Assert.Contains("--single-transaction", startInfo.ArgumentList);
        Assert.Contains("--exit-on-error", startInfo.ArgumentList);
        Assert.Contains("--no-owner", startInfo.ArgumentList);
        Assert.Contains("--no-privileges", startInfo.ArgumentList);

        // The target database is the one named, not the one the connection string points at.
        var databaseIndex = startInfo.ArgumentList.IndexOf("--dbname");
        Assert.Equal("cinomni_restored", startInfo.ArgumentList[databaseIndex + 1]);
        Assert.Equal("/backups/in.dump", startInfo.ArgumentList[^1]);
    }

    /// <summary>
    /// <c>--clean</c> would be the obvious way to "replace whatever is there", and it does not survive
    /// this schema: it emits a <c>DROP CONSTRAINT</c> for a partition's inherited primary key, which
    /// PostgreSQL refuses, and the restore dies part-way through the sweep. The documented procedure
    /// restores into a database created for it, which has no such failure mode and keeps the old
    /// database until the operator is satisfied.
    /// </summary>
    [Fact]
    public void The_restore_never_tries_to_drop_what_is_already_in_the_target()
    {
        var startInfo = PgToolCommand.Restore("pg_restore", Target(), "cinomni_restored", "/backups/in.dump");

        Assert.DoesNotContain("--clean", startInfo.ArgumentList);
        Assert.DoesNotContain("--if-exists", startInfo.ArgumentList);
    }

    /// <summary>
    /// A dump taken over several connections is not one snapshot, and a backup that is inconsistent
    /// between modules is not a backup.
    /// </summary>
    [Fact]
    public void The_dump_is_taken_over_a_single_connection()
    {
        var startInfo = PgToolCommand.Dump("pg_dump", Target(), "out.dump");

        Assert.DoesNotContain(startInfo.ArgumentList, argument =>
            argument.StartsWith("--jobs", StringComparison.Ordinal) || argument == "-j");
    }

    /// <summary>A partial dump is a restore trap; nothing may be excluded from it.</summary>
    [Fact]
    public void Nothing_is_excluded_from_the_dump()
    {
        var startInfo = PgToolCommand.Dump("pg_dump", Target(), "out.dump");

        Assert.DoesNotContain(startInfo.ArgumentList, argument =>
            argument.StartsWith("--exclude", StringComparison.Ordinal)
            || argument.StartsWith("--schema=", StringComparison.Ordinal)
            || argument == "--data-only"
            || argument == "--schema-only");
    }

    [Fact]
    public void Asking_a_tool_for_its_version_carries_no_credential()
    {
        var startInfo = PgToolCommand.Version("pg_dump");

        Assert.Equal(["--version"], startInfo.ArgumentList);
        Assert.False(startInfo.Environment.ContainsKey(PgToolCommand.PasswordVariable));
    }

    /// <summary>A connection string with no password must not put an empty variable in the child's environment.</summary>
    [Fact]
    public void A_target_without_a_password_sets_no_password_variable()
    {
        var target = PgToolCommand.TargetFrom("Host=localhost;Database=cinomni;Username=cinomni");

        var startInfo = PgToolCommand.Dump("pg_dump", target, "out.dump");

        Assert.False(startInfo.Environment.ContainsKey(PgToolCommand.PasswordVariable));
    }
}
