using System.Diagnostics;
using System.Globalization;
using Npgsql;

namespace Cinomni.Operations.Backup;

/// <summary>
/// Where the PostgreSQL client tools should connect, extracted from the installation's connection
/// string. It is a class rather than a record on purpose: a record's generated <c>ToString</c> prints
/// every property, and one careless interpolation would put the database password in a log line.
/// </summary>
public sealed class PgConnectionTarget
{
    public PgConnectionTarget(string host, int port, string username, string database, string? password)
    {
        Host = host;
        Port = port;
        Username = username;
        Database = database;
        Password = password;
    }

    public string Host { get; }

    public int Port { get; }

    public string Username { get; }

    public string Database { get; }

    /// <summary>
    /// The password, handed to the child process through its environment and never through argv —
    /// an argument vector is world-readable on Linux. Never logged, never serialised.
    /// </summary>
    public string? Password { get; }

    /// <summary>Deliberately omits the password, so this type is safe to interpolate anywhere.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Username}@{Host}:{Port}/{Database}");
}

/// <summary>
/// Builds the argument vectors for <c>pg_dump</c> and <c>pg_restore</c>.
/// <para>
/// Separated from the runner so a test can assert the exact vector without launching anything: this is
/// where the process-security rules are visible — every argument is a distinct <c>ArgumentList</c>
/// entry (never a concatenated command line), <c>UseShellExecute</c> is off so shell metacharacters are
/// inert, <c>--no-password</c> makes a missing credential an immediate failure instead of a prompt that
/// hangs a background job for ever, and the password travels in the environment.
/// </para>
/// </summary>
public static class PgToolCommand
{
    /// <summary>The environment variable libpq reads a password from.</summary>
    public const string PasswordVariable = "PGPASSWORD";

    /// <summary>Reads the connection target out of an Npgsql connection string.</summary>
    public static PgConnectionTarget TargetFrom(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);

        return new PgConnectionTarget(
            string.IsNullOrWhiteSpace(builder.Host) ? "localhost" : builder.Host,
            builder.Port,
            builder.Username ?? string.Empty,
            builder.Database ?? string.Empty,
            builder.Password);
    }

    /// <summary>
    /// A full-database dump in PostgreSQL's custom archive format.
    /// <para>
    /// One connection, no <c>--jobs</c>: a single-connection <c>pg_dump</c> reads inside one repeatable-read
    /// snapshot, which is the entire reason the dump is internally consistent across all sixteen schemas.
    /// Parallel dumping would trade that for speed, and a backup that is fast but inconsistent between
    /// modules is not a backup.
    /// </para>
    /// <para>
    /// Nothing is excluded. A partial dump is a restore trap: the operator would find out which table was
    /// missing at the worst possible moment.
    /// </para>
    /// </summary>
    public static ProcessStartInfo Dump(string binaryPath, PgConnectionTarget target, string outputFile)
    {
        var startInfo = Base(binaryPath, target);

        startInfo.ArgumentList.Add("--format=custom");
        startInfo.ArgumentList.Add("--file");
        startInfo.ArgumentList.Add(outputFile);
        AddConnection(startInfo, target, target.Database);

        return startInfo;
    }

    /// <summary>
    /// Restores an archive into <paramref name="database"/>, which must be <b>empty</b>.
    /// <para>
    /// There is deliberately no <c>--clean</c>. It looks like the convenient option — "replace whatever
    /// is there" — and it does not survive this schema: dropping the objects of a partitioned table
    /// emits <c>ALTER TABLE ... DROP CONSTRAINT</c> for a partition's inherited primary key, which
    /// PostgreSQL refuses, and the restore dies half-way through the sweep. Restoring into a database
    /// created for the purpose is the documented procedure precisely because it has no such failure mode,
    /// and it leaves the old database intact until the operator is satisfied with the new one.
    /// </para>
    /// <para>
    /// <c>--single-transaction</c> then makes the outcome binary: the restore lands completely or leaves
    /// the target exactly as it was, and a target that is <em>not</em> empty fails on the first object
    /// that already exists rather than merging two installations. Ownership and privileges are dropped
    /// from the archive because the restoring role need not be the dumping one on a single-owner node.
    /// </para>
    /// </summary>
    public static ProcessStartInfo Restore(
        string binaryPath,
        PgConnectionTarget target,
        string database,
        string inputFile)
    {
        var startInfo = Base(binaryPath, target);

        startInfo.ArgumentList.Add("--no-owner");
        startInfo.ArgumentList.Add("--no-privileges");
        startInfo.ArgumentList.Add("--exit-on-error");
        startInfo.ArgumentList.Add("--single-transaction");
        AddConnection(startInfo, target, database);
        startInfo.ArgumentList.Add(inputFile);

        return startInfo;
    }

    /// <summary>Asks a tool for its version. No connection, no credential.</summary>
    public static ProcessStartInfo Version(string binaryPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = binaryPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add("--version");
        return startInfo;
    }

    private static ProcessStartInfo Base(string binaryPath, PgConnectionTarget target)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = binaryPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        if (!string.IsNullOrEmpty(target.Password))
        {
            startInfo.Environment[PasswordVariable] = target.Password;
        }

        return startInfo;
    }

    private static void AddConnection(ProcessStartInfo startInfo, PgConnectionTarget target, string database)
    {
        startInfo.ArgumentList.Add("--no-password");
        startInfo.ArgumentList.Add("--host");
        startInfo.ArgumentList.Add(target.Host);
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(target.Port.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--username");
        startInfo.ArgumentList.Add(target.Username);
        startInfo.ArgumentList.Add("--dbname");
        startInfo.ArgumentList.Add(database);
    }
}
