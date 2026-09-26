using Cinomni.Kernel.Results;
using Cinomni.Operations.Persistence;
using Cinomni.Operations.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Cinomni.Operations.Backup;

/// <summary>
/// Takes and verifies database backups.
/// <para>
/// A run is a sequence of external effects and never a unit of work: no business transaction is open
/// while a child process writes gigabytes to disk. What it does hold is a PostgreSQL <b>session</b>
/// advisory lock, so a scheduled run and an operator running the CLI at the same time cannot produce
/// two half-written dumps — the second one reports that a backup is already in progress and stops.
/// </para>
/// <para>
/// Consistency comes from <c>pg_dump</c> itself: one connection, one repeatable-read snapshot, all
/// sixteen schemas. Ordering makes a crash harmless — dump into <c>.partial</c>, hash it, rename it
/// into place, write the manifest, and only then apply retention. Any interruption leaves the previous
/// backups untouched and an artefact that is not mistaken for a backup.
/// </para>
/// <para>
/// Every attempt is recorded in <c>operations.backup_run</c> through <see cref="BackupJournal"/>,
/// including the ones that produce nothing. Backup has no HTTP surface, so that table is the only place
/// an operator — or a later health surface — can find out whether this installation is actually backing
/// itself up, rather than inferring it from a directory listing.
/// </para>
/// </summary>
public sealed class BackupService(
    OperationsDbContext dbContext,
    ILiveOptions<BackupOptions> options,
    BackupStore store,
    SchemaInventory inventory,
    IDatabaseDumpRunner runner,
    BackupJournal journal,
    ILogger<BackupService> logger)
{
    /// <summary>
    /// An arbitrary but fixed key in PostgreSQL's advisory-lock namespace, reserved for "a Cinomni
    /// backup of this database is running". Session-scoped, so it is released when the connection
    /// closes even if the process is killed mid-dump. Public so a test can hold it from another session
    /// and so an operator can see who holds it in <c>pg_locks</c>.
    /// </summary>
    public const long AdvisoryLockKey = 6_113_552_419_007L;

    /// <summary>Reported when another run already holds the lock. Not an error: the other run will produce the backup.</summary>
    public const string InProgressCode = "backup.in_progress";

    /// <summary>Reported when the dump, its hash or its manifest could not be written to the backup root.</summary>
    public const string WriteFailedCode = "backup.write_failed";

    /// <summary>
    /// Takes one backup and records what became of it in <c>operations.backup_run</c>. Returns the
    /// manifest that was written.
    /// </summary>
    /// <param name="triggeredBy">One of <see cref="BackupTrigger"/>: what asked for this run.</param>
    /// <param name="cancellationToken">Cancels the run; the journal row is then closed by the next one.</param>
    public async Task<Result<BackupManifest>> CreateAsync(
        string triggeredBy,
        CancellationToken cancellationToken = default)
    {
        // The lock is session-scoped, so the connection has to stay open for the whole run. EF closes
        // it after each command unless it was opened explicitly.
        await dbContext.Database.OpenConnectionAsync(cancellationToken);

        try
        {
            var startedAt = DateTimeOffset.UtcNow;

            if (!await TryAcquireLockAsync(cancellationToken))
            {
                var busy = new Error(InProgressCode, "Another backup is already running on this database.");

                // A warning, not information: this tick produced no backup, and the row below is what
                // keeps that distinguishable from one that did.
                logger.LogWarning("A backup is already running; this run produced nothing.");
                await journal.RecordSkippedAsync(triggeredBy, busy, startedAt, cancellationToken);

                return Result<BackupManifest>.Failure(busy);
            }

            try
            {
                // Holding the lock is proof that no other run is live, so any row still open belongs to
                // a run that died. Closing them here needs no startup hook and cannot touch a live run.
                await journal.CloseAbandonedAsync(startedAt, cancellationToken);

                var run = await journal.BeginAsync(triggeredBy, startedAt, cancellationToken);
                var outcome = await RunAsync(startedAt, cancellationToken);

                if (outcome.IsSuccess)
                {
                    await journal.CompleteAsync(
                        run,
                        BackupStore.StampFor(startedAt),
                        outcome.Value,
                        DateTimeOffset.UtcNow,
                        cancellationToken);
                }
                else
                {
                    // Two runs inside the same second land here with the in-progress code, and that is
                    // the same "produced nothing on purpose" as losing the lock.
                    var skipped = string.Equals(outcome.Error.Code, InProgressCode, StringComparison.Ordinal);

                    await journal.FailAsync(
                        run,
                        skipped ? BackupRunOutcome.Skipped : BackupRunOutcome.Failed,
                        outcome.Error,
                        DateTimeOffset.UtcNow,
                        cancellationToken);
                }

                return outcome;
            }
            finally
            {
                await ReleaseLockAsync();
            }
        }
        finally
        {
            await dbContext.Database.CloseConnectionAsync();
        }
    }

    /// <summary>
    /// Re-reads a backup's manifest and re-hashes its dump. This is the cheap check an operator runs
    /// before a restore, and the one that catches a truncated copy off the machine.
    /// </summary>
    public async Task<Result<BackupManifest>> VerifyAsync(
        BackupSet set,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(set);

        var manifest = await ReadManifestAsync(set.ManifestPath, cancellationToken);
        if (manifest is null)
        {
            return Result<BackupManifest>.Failure(new Error(
                "backup.manifest_unreadable",
                $"The manifest of backup '{set.Stamp}' is missing or not a Cinomni backup manifest."));
        }

        var actual = await BackupStore.ComputeSha256Async(set.DumpPath, cancellationToken);
        if (!string.Equals(actual, manifest.DumpSha256, StringComparison.Ordinal))
        {
            return Result<BackupManifest>.Failure(new Error(
                "backup.corrupt",
                $"Backup '{set.Stamp}' does not match its manifest: the dump hashes to {actual} but the "
                + $"manifest records {manifest.DumpSha256}. Do not restore it."));
        }

        return Result<BackupManifest>.Success(manifest);
    }

    /// <summary>Reads a manifest from disk, or <c>null</c> when the file is absent or malformed.</summary>
    public static async Task<BackupManifest?> ReadManifestAsync(
        string manifestPath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        var json = await File.ReadAllTextAsync(manifestPath, cancellationToken);
        return BackupManifest.TryParse(json);
    }

    /// <summary>
    /// One run, with the filesystem's failures turned into results. A backup root that filled up, or
    /// that a bind mount handed to another account, is an operational state the journal has to keep and
    /// the CLI has to print — not a stack trace out of a background worker.
    /// </summary>
    private async Task<Result<BackupManifest>> RunAsync(DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        var prepared = store.EnsureRoot();
        if (prepared.IsFailure)
        {
            return Result<BackupManifest>.Failure(prepared.Error);
        }

        try
        {
            return await WriteAsync(startedAt, cancellationToken);
        }
        catch (Exception exception) when (BackupStore.IsFilesystemFailure(exception))
        {
            logger.LogWarning(exception, "The backup run could not write to {BackupRoot}.", store.Root);

            return Result<BackupManifest>.Failure(new Error(
                WriteFailedCode,
                $"The backup could not be written to '{store.Root}': {exception.Message} Check the free "
                + "space and the ownership of the backup root."));
        }
    }

    private async Task<Result<BackupManifest>> WriteAsync(DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        // Before anything is written, not only after a run succeeds. Retention proper is gated on
        // success so a broken backup never costs a good one, but a run that never succeeds would then
        // never reclaim anything: each failed attempt leaves a partial of its own, and on a filling disk
        // the space the next success needs is the space those partials are holding.
        store.SweepIncomplete(startedAt);

        var stamp = BackupStore.StampFor(startedAt);
        var partialPath = store.PartialPathFor(stamp);
        var dumpPath = store.DumpPathFor(stamp);

        if (File.Exists(dumpPath))
        {
            // Two runs inside the same second. The lock makes this all but impossible, and the honest
            // answer is still to refuse rather than overwrite a backup that already exists.
            return Result<BackupManifest>.Failure(new Error(
                InProgressCode, $"A backup named '{stamp}' already exists."));
        }

        // Read the schema state before the dump, so the manifest can only ever understate what the
        // archive contains. A migration applied between the two would appear in the dump and not in
        // the manifest, which makes the restore gate refuse — the safe direction.
        var state = await inventory.ReadAsync(cancellationToken);
        var serverVersion = ((NpgsqlConnection)dbContext.Database.GetDbConnection()).PostgreSqlVersion.ToString();
        var databaseName = dbContext.Database.GetDbConnection().Database;

        var toolVersion = await runner.ProbeVersionAsync(cancellationToken);
        if (toolVersion.IsFailure)
        {
            return Result<BackupManifest>.Failure(toolVersion.Error);
        }

        WarnOnMismatchedToolMajor(toolVersion.Value, serverVersion);

        // Create the file before the tool does, with this account's permissions already on it. Both
        // tools open --file for writing without truncating the mode, so the dump is never briefly
        // world-readable while it is being written — and it is at its largest and most interesting
        // exactly then.
        await File.WriteAllBytesAsync(partialPath, [], cancellationToken);
        BackupStore.RestrictPermissions(partialPath);

        var dump = await runner.DumpAsync(partialPath, cancellationToken);
        if (dump.IsFailure)
        {
            // The partial file is left where it is: it is evidence, and the retention sweep removes it
            // once it is old enough to be certain no run is still writing to it.
            return Result<BackupManifest>.Failure(dump.Error);
        }

        BackupStore.RestrictPermissions(partialPath);
        var size = new FileInfo(partialPath).Length;
        var sha256 = await BackupStore.ComputeSha256Async(partialPath, cancellationToken);

        BackupStore.Publish(partialPath, dumpPath);

        var manifest = new BackupManifest
        {
            FormatVersion = BackupManifest.CurrentFormatVersion,
            CreatedAt = startedAt,
            DatabaseName = databaseName,
            ServerVersion = serverVersion,
            ToolVersion = toolVersion.Value,
            DumpFileName = Path.GetFileName(dumpPath),
            DumpSizeBytes = size,
            DumpSha256 = sha256,
            Schemas = state.Schemas,
            UnrecognizedMigrations = state.UnrecognizedMigrations,
        };

        var manifestPath = store.ManifestPathFor(stamp);
        await File.WriteAllTextAsync(manifestPath, manifest.ToJson(), cancellationToken);
        BackupStore.RestrictPermissions(manifestPath);

        logger.LogInformation(
            "Backup {Backup} written: {Bytes} bytes covering {SchemaCount} schemas.",
            stamp,
            size,
            state.Schemas.Count);

        if (state.UnrecognizedMigrations.Count > 0)
        {
            // Not a failure — the dump is complete and correct. It is a warning because this build
            // could not restore it: something applied migrations nothing here carries.
            logger.LogWarning(
                "The database has {Count} applied migration(s) no module in this build owns: {Migrations}. "
                + "A restore of this backup into this build will be refused.",
                state.UnrecognizedMigrations.Count,
                string.Join(", ", state.UnrecognizedMigrations));
        }

        // Only after a complete set exists. A failed run must never be the reason an old backup goes.
        // Read at use time, never cached: a value the settings store just accepted must be honoured by
        // the very next run, not only after a restart.
        store.ApplyRetention(options.Current.KeepCount, DateTimeOffset.UtcNow);

        return Result<BackupManifest>.Success(manifest);
    }

    /// <summary>
    /// Reports a client whose major version is not the server's.
    /// <para>
    /// It is not a failure — <c>pg_dump</c> from a newer client reads an older server perfectly well, and
    /// the archive it writes is sound. What breaks is the far end: <c>pg_restore</c> emits the preamble
    /// its own version uses, so a newer client restoring into an older server aborts on a setting that
    /// server has never heard of. The pairing rule is therefore "the client's major version is the
    /// server's", and an installation that drifts from it finds out here, months before it needs the
    /// backup, instead of during the restore.
    /// </para>
    /// </summary>
    private void WarnOnMismatchedToolMajor(string toolVersion, string serverVersion)
    {
        var tool = MajorVersion(toolVersion);
        var server = MajorVersion(serverVersion);

        if (tool is null || server is null || tool == server)
        {
            return;
        }

        logger.LogWarning(
            "The backup client reports '{ToolVersion}' and the server is {ServerVersion}. The dump is "
            + "valid, but restoring it needs a client of the server's major version — install "
            + "postgresql-client {ServerMajor} on the machine that will run the restore.",
            toolVersion,
            serverVersion,
            server);
    }

    /// <summary>Leading major number of a version string such as <c>16.4</c> or <c>pg_dump (PostgreSQL) 17.10</c>.</summary>
    private static int? MajorVersion(string version)
    {
        var digits = version.SkipWhile(character => !char.IsAsciiDigit(character)).TakeWhile(char.IsAsciiDigit).ToArray();
        return digits.Length > 0 && int.TryParse(digits, System.Globalization.CultureInfo.InvariantCulture, out var major)
            ? major
            : null;
    }

    private async Task<bool> TryAcquireLockAsync(CancellationToken cancellationToken)
    {
        var acquired = await dbContext.Database
            .SqlQuery<bool>($"SELECT pg_try_advisory_lock({AdvisoryLockKey}) AS \"Value\"")
            .ToListAsync(cancellationToken);

        return acquired.Count > 0 && acquired[0];
    }

    private async Task ReleaseLockAsync()
    {
        try
        {
            // No cancellation token: releasing the lock must happen even on a cancelled run, and the
            // session lock would otherwise survive until the pooled connection is recycled.
            await dbContext.Database
                .SqlQuery<bool>($"SELECT pg_advisory_unlock({AdvisoryLockKey}) AS \"Value\"")
                .ToListAsync(CancellationToken.None);
        }
        catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException)
        {
            // The connection is already gone, which released the lock anyway.
            logger.LogDebug(exception, "Could not release the backup advisory lock; the session is closed.");
        }
    }
}
