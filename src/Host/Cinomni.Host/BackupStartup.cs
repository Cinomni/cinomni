using Cinomni.Downloads.Engine;
using Cinomni.Import.Application;
using Cinomni.Operations.Backup;
using Cinomni.Playback.Application;

namespace Cinomni.Host;

/// <summary>
/// The startup half of the backup contract, separate from <see cref="StartupChecks"/> because the two
/// answer different questions and run under different rules.
/// <para>
/// <see cref="VerifyRootIsSeparate"/> is pure path arithmetic and therefore runs in <b>every</b>
/// environment, not only in Production: a backup root that overlaps a media root is a configuration
/// mistake with a security consequence — a dump full of delivery-channel URLs and password verifiers
/// inside a tree the library serves from — and a developer must hit it as hard as an operator does.
/// </para>
/// <para>
/// The tool check is a warning, on the same reasoning as the FFmpeg one. An installation whose
/// <c>pg_dump</c> is missing still browses, plays and imports; what it does not do is back itself up,
/// and the backup worker records that in <c>operations.backup_run</c> where it can be found later.
/// </para>
/// </summary>
internal static class BackupStartup
{
    /// <summary>
    /// Refuses a backup root that overlaps any storage root, in either direction, naming both keys.
    /// </summary>
    /// <exception cref="InvalidOperationException">The roots overlap.</exception>
    public static void VerifyRootIsSeparate(IServiceProvider services)
    {
        var backup = services.GetRequiredService<BackupOptions>();

        BackupRoot.VerifySeparateFrom(
            backup.Root,
            [
                ("Import:LibraryRoot", services.GetRequiredService<ImportOptions>().LibraryRoot),
                ("Downloads:Sidecar:StagingPath", services.GetRequiredService<SidecarOptions>().StagingPath),
                ("Playback:TranscodeRoot", services.GetRequiredService<PlaybackOptions>().TranscodeRoot),
            ]);
    }

    /// <summary>
    /// Reports where backups will be written — the resolved absolute path, because <c>Backup:Root</c>
    /// accepts a relative one — and warns when the tool that produces them cannot be found.
    /// </summary>
    public static void ReportBackupContract(IServiceProvider services, ILogger logger)
    {
        var options = services.GetRequiredService<BackupOptions>();

        if (options.Enabled)
        {
            logger.LogInformation(
                "Backups are written to {BackupRoot}, keeping the newest {KeepCount}, every {Interval}.",
                options.Root,
                options.KeepCount,
                options.Interval);

            // A warning rather than a crash, on the transcode-root precedent: an installation whose
            // backup directory is owned by somebody else still browses, plays and imports. What it must
            // not do is say "backups are written to /data/backups" and leave the operator to discover a
            // day later that none were. The run itself reports the same failure through the journal.
            StartupChecks.WarnWhenRootNotWritable(logger, "Backup:Root", options.Root);
        }
        else
        {
            logger.LogWarning(
                "Scheduled backups are disabled (Backup:Enabled=false). Nothing will back this installation "
                + "up on its own; take one with 'backup create' before an upgrade.");
        }

        StartupChecks.WarnWhenExecutableMissing(logger, "Backup:PgDumpPath", options.PgDumpPath);
    }
}
