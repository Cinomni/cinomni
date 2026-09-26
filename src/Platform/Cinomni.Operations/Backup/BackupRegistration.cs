using Cinomni.Operations.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cinomni.Operations.Backup;

/// <summary>
/// Registers database backup on the platform kernel: the options, the store, the run journal, the
/// manifest inventory, the external tool adapter, and — when enabled — the worker that takes one.
/// <para>
/// Separate from <c>AddOperations</c> because the tool adapter is an external boundary and belongs to
/// the same "registered on its own so a test can substitute it" rule every module adapter follows. It
/// adds no ASP.NET dependency to the platform: <b>backup has no HTTP surface at all</b>. A dump carries
/// delivery-channel URLs, indexer addresses that commonly embed keys, Argon2id password verifiers and
/// session token hashes, so a single authorization slip on a download route would hand over the whole
/// installation. The operator reaches a backup through the filesystem and the CLI verbs, which are
/// already gated by shell access to the node.
/// </para>
/// </summary>
public static class BackupRegistration
{
    /// <param name="services">The composition root's service collection.</param>
    /// <param name="connectionString">
    /// The same PostgreSQL connection every module shares. The tools need host, port, user, database and
    /// password as separate values, so this is parsed once inside the adapter and the password never
    /// leaves it.
    /// </param>
    /// <param name="configure">
    /// Applied to the defaults before they are validated and frozen. Read at registration time, because
    /// the cadence becomes the worker's interval and the root has to be settled before anything writes.
    /// </param>
    public static IServiceCollection AddBackup(
        this IServiceCollection services,
        string connectionString,
        Action<BackupOptions>? configure = null)
    {
        var options = new BackupOptions();
        configure?.Invoke(options);
        options.Validate();

        services.AddSingleton(options);

        // The settings store's catalogue for BackupOptions: only KeepCount and Interval carry a
        // settings key today. The binder closes over the already-canonicalised `options` for everything
        // else (Enabled, Root, the tool paths, Timeout) — none of which is browser-editable (a dump's
        // storage location is a deployment fact, and the tool paths are executables launched as a
        // process). Unlike every other retention owner's Interval, BackupHostedService re-reads
        // Interval on every tick of its own loop, so a stored change here really does apply without a
        // restart.
        BackupOptions BindOptions(SettingsView view) => new()
        {
            Enabled = options.Enabled,
            Root = options.Root,
            KeepCount = view.GetInt(BackupSettingDefinitions.KeepCount) ?? options.KeepCount,
            Interval = view.GetTimeSpan(BackupSettingDefinitions.Interval) ?? options.Interval,
            PgDumpPath = options.PgDumpPath,
            PgRestorePath = options.PgRestorePath,
            Timeout = options.Timeout,
        };

        services.AddSettingsCheck(BackupSettingDefinitions.All, BindOptions, BackupOptions.Check);
        services.AddLiveOptions(BindOptions);

        services.AddSingleton<BackupStore>();
        services.AddScoped<SchemaInventory>();
        services.AddScoped<BackupJournal>();
        services.AddScoped<BackupService>();

        services.AddSingleton<IDatabaseDumpRunner>(provider => new PgToolDumpRunner(
            connectionString,
            options,
            provider.GetRequiredService<ILogger<PgToolDumpRunner>>()));

        if (options.Enabled)
        {
            // A worker of its own rather than a scheduled job: see BackupHostedService for why a dump
            // must never occupy the single command worker.
            services.AddHostedService<BackupHostedService>();
        }

        return services;
    }
}
