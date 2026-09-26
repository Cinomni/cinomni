using Cinomni.Acquisition.Persistence;
using Cinomni.Catalog.Persistence;
using Cinomni.Decision.Persistence;
using Cinomni.Discovery.Persistence;
using Cinomni.Downloads.Persistence;
using Cinomni.Identity.Persistence;
using Cinomni.Import.Persistence;
using Cinomni.Library.Persistence;
using Cinomni.Metadata.Persistence;
using Cinomni.Monitoring.Persistence;
using Cinomni.Notifications.Persistence;
using Cinomni.Operations.Backup;
using Cinomni.Operations.Persistence;
using Cinomni.Playback.Persistence;
using Cinomni.ReleaseParsing.Persistence;
using Cinomni.Requests.Persistence;
using Cinomni.Subtitles.Persistence;

namespace Cinomni.Host;

/// <summary>
/// Binds the <c>Backup</c> configuration section and declares which schemas a backup covers.
/// <para>
/// This is the only place in the installation allowed to name every module's context, which is exactly
/// why the schema list lives here rather than in the platform: <c>Cinomni.Operations</c> must not know a
/// module exists. Each entry uses the context's own <c>SchemaName</c> constant, so renaming a schema
/// cannot silently desync the manifest from the database.
/// </para>
/// <para>
/// The order matches the migrate sequence in <c>Program</c>. RealTime is deliberately absent: it owns no
/// schema and no context. Sixteen sources for sixteen schemas — <c>BackupWiringTests</c> in the Host test
/// project fails the build if that stops being true.
/// </para>
/// <para>
/// Every value is read explicitly rather than through a blanket <c>Bind</c>, following
/// <see cref="ModuleConfiguration"/>: a typo then leaves the default in place instead of silently
/// repointing where backups are written.
/// </para>
/// </summary>
internal static class BackupComposition
{
    private const string SectionName = "Backup";

    public static IServiceCollection AddConfiguredBackup(
        this IServiceCollection services,
        IConfiguration configuration,
        string connectionString)
    {
        var section = configuration.GetSection(SectionName);

        services.AddBackup(connectionString, options =>
        {
            options.Enabled = section.GetValue("Enabled", options.Enabled);
            options.Root = Text(section, "Root") ?? options.Root;
            options.KeepCount = section.GetValue("KeepCount", options.KeepCount);
            options.Interval = section.GetValue<TimeSpan?>("Interval") ?? options.Interval;
            options.Timeout = section.GetValue<TimeSpan?>("Timeout") ?? options.Timeout;
            options.PgDumpPath = Text(section, "PgDumpPath") ?? options.PgDumpPath;
            options.PgRestorePath = Text(section, "PgRestorePath") ?? options.PgRestorePath;
        });

        return services.AddBackupSchemas();
    }

    /// <summary>
    /// The sixteen schemas a manifest records, in migrate order. Exposed on its own so the wiring test
    /// reads the real list instead of a copy that drifts from it.
    /// </summary>
    public static IServiceCollection AddBackupSchemas(this IServiceCollection services) => services
        .AddBackupSchema<OperationsDbContext>(OperationsDbContext.SchemaName)
        .AddBackupSchema<IdentityDbContext>(IdentityDbContext.SchemaName)
        .AddBackupSchema<CatalogDbContext>(CatalogDbContext.SchemaName)
        .AddBackupSchema<MetadataDbContext>(MetadataDbContext.SchemaName)
        .AddBackupSchema<MonitoringDbContext>(MonitoringDbContext.SchemaName)
        .AddBackupSchema<DiscoveryDbContext>(DiscoveryDbContext.SchemaName)
        .AddBackupSchema<ReleaseParsingDbContext>(ReleaseParsingDbContext.SchemaName)
        .AddBackupSchema<DecisionDbContext>(DecisionDbContext.SchemaName)
        .AddBackupSchema<AcquisitionDbContext>(AcquisitionDbContext.SchemaName)
        .AddBackupSchema<DownloadsDbContext>(DownloadsDbContext.SchemaName)
        .AddBackupSchema<ImportDbContext>(ImportDbContext.SchemaName)
        .AddBackupSchema<LibraryDbContext>(LibraryDbContext.SchemaName)
        .AddBackupSchema<PlaybackDbContext>(PlaybackDbContext.SchemaName)
        .AddBackupSchema<SubtitlesDbContext>(SubtitlesDbContext.SchemaName)
        .AddBackupSchema<RequestsDbContext>(RequestsDbContext.SchemaName)
        .AddBackupSchema<NotificationsDbContext>(NotificationsDbContext.SchemaName);

    /// <summary>A configured string, or null when the key is absent or blank (a blank key is "not set").</summary>
    private static string? Text(IConfiguration section, string key)
    {
        var value = section[key];
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
