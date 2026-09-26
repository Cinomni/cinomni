using Npgsql;

namespace Cinomni.Host.Tests;

/// <summary>
/// A fact that needs two filesystems one unprivileged process can reach, which on Linux is the
/// shared-memory tmpfs beside the disk-backed temporary directory. Windows has no equivalent pair: a
/// second volume means mounting one, which needs administrative rights a test cannot assume.
/// <para>
/// xUnit 2.9.3 cannot report a test as skipped from inside its body, so a machine that cannot host the
/// case used to make the test <c>return</c> before asserting — which the runner reports as
/// <b>passed</b>. The decision belongs here instead, where it is reported as a skip with its reason.
/// </para>
/// </summary>
public sealed class SecondFilesystemFactAttribute : FactAttribute
{
    /// <summary>The tmpfs every Linux distribution mounts, and the second filesystem this needs.</summary>
    public const string SharedMemory = "/dev/shm";

    public SecondFilesystemFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "This needs two filesystems one unprivileged process can reach. Only Linux has a pair "
                + "(/dev/shm beside the temporary directory); a second volume on Windows means mounting "
                + "one, which needs administrative rights.";
        }
        else if (!Directory.Exists(SharedMemory))
        {
            Skip = $"This Linux installation has no {SharedMemory}, so there is no second filesystem to "
                + "put the other storage root on.";
        }
    }
}

/// <summary>
/// A fact that needs everything the restore drill launches: the PostgreSQL client tools and a reachable
/// server. Where either is missing the drill is reported as skipped, naming which — unless the run is one
/// that must prove the drill, in which case nothing is skipped and the test itself fails by name.
/// </summary>
public sealed class BackupDrillFactAttribute : FactAttribute
{
    public BackupDrillFactAttribute()
    {
        if (!BackupDrillPrerequisites.IsMandatory && BackupDrillPrerequisites.UnmetReason() is { } reason)
        {
            Skip = reason;
        }
    }
}

/// <summary>
/// What the restore drill needs from the machine, asked once and in one place, so the attribute that
/// decides whether to run it and the test that fails when it must run agree by construction.
/// </summary>
internal static class BackupDrillPrerequisites
{
    /// <summary>Set it to <c>1</c> to turn a missing prerequisite into a failure instead of a skip.</summary>
    private const string RequireVariable = "CINOMNI_REQUIRE_BACKUP_DRILL";

    /// <summary>The development server, with a short timeout so a machine without one is not held up.</summary>
    public const string ServerConnectionString =
        "Host=localhost;Port=5442;Username=cinomni;Password=cinomni_dev;Timeout=5";

    /// <summary>
    /// Whether a missing prerequisite must fail rather than be skipped. Opt-in rather than "any CI run",
    /// because this project is also run by the job that has neither a database nor the client — there it
    /// genuinely cannot run the drill, and failing would say something untrue about the change under
    /// test. The job that <em>does</em> have both sets it, and its workflow says why.
    /// </summary>
    public static bool IsMandatory => Environment.GetEnvironmentVariable(RequireVariable) == "1";

    /// <summary>
    /// Which client binary the drill launches, read from the configuration key's own environment variable
    /// so a pipeline can pin it. It matters here more than anywhere else: on Debian and Ubuntu the bare
    /// names go through <c>pg_wrapper</c>, which runs the highest installed major rather than the one the
    /// package added, and a <c>pg_restore</c> newer than the server aborts on a preamble that server has
    /// never heard of — the failure this drill exists to catch. Only these two keys are read; binding the
    /// whole environment into a test host would let an unrelated variable rewrite the installation under
    /// test.
    /// </summary>
    public static string ToolPath(string variable, string fallback) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } configured ? configured : fallback;

    /// <summary>
    /// Why the drill cannot run on this machine, or <see langword="null"/> when it can. Asked once: the
    /// answer decides discovery and is then read again by the test itself.
    /// </summary>
    public static string? UnmetReason() => LazyUnmetReason.Value;

    private static readonly Lazy<string?> LazyUnmetReason = new(Probe);

    private static string? Probe()
    {
        foreach (var (key, variable, fallback) in new[]
        {
            ("Backup:PgDumpPath", "Backup__PgDumpPath", "pg_dump"),
            ("Backup:PgRestorePath", "Backup__PgRestorePath", "pg_restore"),
        })
        {
            var binary = ToolPath(variable, fallback);
            if (!StartupChecks.CanResolveExecutable(binary))
            {
                return $"The restore drill needs '{binary}' ({key}) on PATH. Install postgresql-client 16 "
                    + "or newer, or point Backup:PgDumpPath and Backup:PgRestorePath at one.";
            }
        }

        try
        {
            using var connection = new NpgsqlConnection(ServerConnectionString + ";Database=postgres");
            connection.Open();
        }
        catch (NpgsqlException exception)
        {
            return "The restore drill needs PostgreSQL on localhost:5442 "
                + $"(docker compose -f docker-compose.dev.yml up -d): {exception.Message}";
        }

        return null;
    }
}
