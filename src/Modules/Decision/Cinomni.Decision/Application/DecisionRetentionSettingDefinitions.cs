using Cinomni.Operations.Settings;

namespace Cinomni.Decision.Application;

/// <summary>
/// The settable-key catalogue backed by <see cref="DecisionRetentionOptions"/>: exactly the two
/// properties its own <see cref="DecisionRetentionOptions.Check"/> already names and validates.
/// </summary>
public static class DecisionRetentionSettingDefinitions
{
    public static readonly SettingDefinition EvaluationRetention = new(
        DecisionRetentionOptions.EvaluationRetentionKey,
        SettingKind.Duration,
        IsSecret: false,
        "Retention:Decision:EvaluationRetention",
        "90.00:00:00",
        SettingValidationPresets.Duration);

    /// <summary>
    /// Persists and audits like every other setting here, but the running purge cadence only picks it
    /// up after a restart: <c>ScheduledJobRegistration</c> captures the interval once, at composition
    /// (see <c>DecisionModule.AddDecisionModule</c>).
    /// </summary>
    public static readonly SettingDefinition Interval = new(
        DecisionRetentionOptions.IntervalKey,
        SettingKind.Duration,
        IsSecret: false,
        "Retention:Decision:Interval",
        "1.00:00:00",
        SettingValidationPresets.Duration);

    public static readonly IReadOnlyList<SettingDefinition> All = [EvaluationRetention, Interval];
}
