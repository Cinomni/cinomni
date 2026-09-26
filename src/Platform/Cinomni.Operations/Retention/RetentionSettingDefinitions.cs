using Cinomni.Operations.Settings;

namespace Cinomni.Operations.Retention;

/// <summary>
/// The settable-key catalogue backed by <see cref="RetentionOptions"/>: exactly the five properties
/// its own <see cref="RetentionOptions.Check"/> already names and validates — no more, no less. Every
/// key, configuration path and default matches what <see cref="RetentionConfiguration"/> in the Host
/// already binds today, so a fresh installation with no stored override reads identically through the
/// settings store as it did as a plain configuration-bound singleton.
/// </summary>
public static class RetentionSettingDefinitions
{
    public static readonly SettingDefinition OutboxRetention = new(
        RetentionOptions.OutboxRetentionKey,
        SettingKind.Duration,
        IsSecret: false,
        "Retention:OutboxRetention",
        "14.00:00:00",
        SettingValidationPresets.Duration);

    public static readonly SettingDefinition CompletedCommandRetention = new(
        RetentionOptions.CompletedCommandRetentionKey,
        SettingKind.Duration,
        IsSecret: false,
        "Retention:CompletedCommandRetention",
        "30.00:00:00",
        SettingValidationPresets.Duration);

    public static readonly SettingDefinition FailedCommandRetention = new(
        RetentionOptions.FailedCommandRetentionKey,
        SettingKind.Duration,
        IsSecret: false,
        "Retention:FailedCommandRetention",
        "180.00:00:00",
        SettingValidationPresets.Duration);

    public static readonly SettingDefinition BatchSize = new(
        RetentionOptions.BatchSizeKey,
        SettingKind.Number,
        IsSecret: false,
        "Retention:BatchSize",
        "5000",
        SettingValidationPresets.Count);

    /// <summary>
    /// Persists and audits like every other setting here, but the running purge cadence only picks it
    /// up after a restart: <c>ScheduledJobRegistration</c> captures the interval once, at composition
    /// (see <c>OperationsModule.AddOperations</c>).
    /// </summary>
    public static readonly SettingDefinition Interval = new(
        RetentionOptions.IntervalKey,
        SettingKind.Duration,
        IsSecret: false,
        "Retention:Interval",
        "1.00:00:00",
        SettingValidationPresets.Duration);

    public static readonly IReadOnlyList<SettingDefinition> All =
    [
        OutboxRetention, CompletedCommandRetention, FailedCommandRetention, BatchSize, Interval,
    ];
}
