using Cinomni.Operations.Settings;

namespace Cinomni.Metadata.Application;

/// <summary>
/// The settable-key catalogue backed by <see cref="MetadataOptions"/>: exactly the two properties its
/// own <see cref="MetadataOptions.Check"/> already names and validates. Every other property on
/// <see cref="MetadataOptions"/> (providers, TTLs, backoff, language, …) has no settings key today and
/// stays bound from configuration only.
/// </summary>
public static class MetadataSettingDefinitions
{
    public static readonly SettingDefinition SnapshotRetention = new(
        MetadataOptions.SnapshotRetentionKey,
        SettingKind.Duration,
        IsSecret: false,
        "Retention:Metadata:SnapshotRetention",
        "30.00:00:00",
        SettingValidationPresets.Duration);

    /// <summary>
    /// Persists and audits like every other setting here, but the running purge cadence only picks it
    /// up after a restart: <c>ScheduledJobRegistration</c> captures the interval once, at composition
    /// (see <c>MetadataModule.AddMetadataAdapters</c>).
    /// </summary>
    public static readonly SettingDefinition SnapshotPurgeInterval = new(
        MetadataOptions.SnapshotPurgeIntervalKey,
        SettingKind.Duration,
        IsSecret: false,
        "Retention:Metadata:Interval",
        "1.00:00:00",
        SettingValidationPresets.Duration);

    public static readonly IReadOnlyList<SettingDefinition> All = [SnapshotRetention, SnapshotPurgeInterval];
}
