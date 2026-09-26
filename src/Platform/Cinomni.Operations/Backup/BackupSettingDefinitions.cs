using Cinomni.Operations.Settings;

namespace Cinomni.Operations.Backup;

/// <summary>
/// The settable-key catalogue backed by <see cref="BackupOptions"/>: exactly the two properties its
/// own <see cref="BackupOptions.Check"/> already names and validates.
/// <para>
/// <see cref="BackupOptions.Root"/>, <see cref="BackupOptions.Timeout"/>,
/// <see cref="BackupOptions.PgDumpPath"/> and <see cref="BackupOptions.PgRestorePath"/> are
/// deliberately excluded — a dump's storage location is a deployment fact, and the tool paths are
/// executables launched as a process, so making either browser-editable would open a path/executable
/// injection surface.
/// </para>
/// </summary>
public static class BackupSettingDefinitions
{
    public static readonly SettingDefinition KeepCount = new(
        BackupOptions.KeepCountKey,
        SettingKind.Number,
        IsSecret: false,
        "Backup:KeepCount",
        "7",
        SettingValidationPresets.Count);

    /// <summary>
    /// Unlike every other <c>*.interval</c> key this increment wires, this one really does apply
    /// without a restart: <see cref="BackupHostedService"/> re-reads it on every tick of its own loop
    /// instead of through a <c>ScheduledJobRegistration</c> captured once at composition (a dump must
    /// never occupy the shared command worker — see <see cref="BackupHostedService"/>).
    /// </summary>
    public static readonly SettingDefinition Interval = new(
        BackupOptions.IntervalKey,
        SettingKind.Duration,
        IsSecret: false,
        "Backup:Interval",
        "1.00:00:00",
        SettingValidationPresets.Duration);

    public static readonly IReadOnlyList<SettingDefinition> All = [KeepCount, Interval];
}
