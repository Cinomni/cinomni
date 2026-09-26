using Cinomni.Kernel.Results;

namespace Cinomni.Operations.Backup;

/// <summary>
/// The external tool boundary of the backup feature. Registered separately from
/// <see cref="BackupService"/> so a test can substitute it, and kept behind an interface because
/// launching a process is an external effect that must never sit inside a unit of work.
/// </summary>
public interface IDatabaseDumpRunner
{
    /// <summary>
    /// Dumps the configured database into <paramref name="targetFile"/>. Returns a failure result —
    /// not an exception — for a tool that is missing, refused a connection or exited non-zero, because
    /// all three are expected operational states the backup worker has to report rather than crash on.
    /// </summary>
    Task<Result> DumpAsync(string targetFile, CancellationToken cancellationToken = default);

    /// <summary>
    /// Restores <paramref name="sourceFile"/> into <paramref name="databaseName"/>, which must be an
    /// <b>empty</b> database. A target holding a previous installation fails on the first object that
    /// already exists rather than merging two of them.
    /// <para>
    /// The operator-facing restore is <c>scripts/cinomni-restore.sh</c>, not this method: a restore
    /// drops and recreates every schema, so the Host must be stopped for it, and no in-process code can
    /// promise that about itself. This exists so the restore drill exercises the same hardened launcher
    /// the script invokes by hand, instead of a second copy written for the test.
    /// </para>
    /// </summary>
    Task<Result> RestoreAsync(string sourceFile, string databaseName, CancellationToken cancellationToken = default);

    /// <summary>The dump tool's self-reported version, recorded in the manifest.</summary>
    Task<Result<string>> ProbeVersionAsync(CancellationToken cancellationToken = default);
}
