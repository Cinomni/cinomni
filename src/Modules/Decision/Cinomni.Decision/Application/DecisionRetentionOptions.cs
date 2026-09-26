using Cinomni.Operations.Settings;

namespace Cinomni.Decision.Application;

/// <summary>
/// How long the evaluation trail is kept — and, much more importantly, what is kept for ever.
/// <para>
/// An accepted verdict is the answer to "why is this file on disk", and a manual override is the
/// answer to "why did we take a release the profile refused". Both are product guarantees, not
/// housekeeping, so no window applies to them at all. Only the noise is aged out: rejections from
/// sweeps that found nothing worth taking.
/// </para>
/// </summary>
public sealed class DecisionRetentionOptions
{
    /// <summary>
    /// Minimum age of a plain rejected evaluation before it may be removed. Accepted verdicts and
    /// evaluations carrying a manual override are exempt at any age.
    /// </summary>
    public TimeSpan EvaluationRetention { get; set; } = TimeSpan.FromDays(90);

    /// <summary>How often <c>decision.retention</c> runs.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromDays(1);

    /// <summary>
    /// Internal (not private) so <see cref="DecisionRetentionSettingDefinitions"/> names the exact same
    /// string rather than a duplicated literal that could drift from it.
    /// </summary>
    internal const string EvaluationRetentionKey = "decision.evaluationRetention";
    internal const string IntervalKey = "retention.decision.interval";

    /// <summary>
    /// The throwing wrapper over <see cref="Check"/>: same rules, same order, same messages — see
    /// <see cref="RetentionOptions.Check"/>'s remarks in <c>Cinomni.Operations.Retention</c> for why the
    /// two must never drift.
    /// </summary>
    /// <exception cref="InvalidOperationException">The configured values are unusable.</exception>
    public void Validate()
    {
        foreach (var error in Check(this))
        {
            throw new InvalidOperationException(error.Message);
        }
    }

    /// <summary>The rules themselves, as pure data — see <see cref="Validate"/>.</summary>
    public static IEnumerable<SettingError> Check(DecisionRetentionOptions candidate)
    {
        if (candidate.EvaluationRetention <= TimeSpan.Zero)
        {
            yield return new SettingError(
                [EvaluationRetentionKey],
                "settings.invalid_value",
                $"Retention:Decision:{nameof(EvaluationRetention)} must be a positive duration "
                + $"(configured: {candidate.EvaluationRetention}).");
        }

        if (candidate.Interval <= TimeSpan.Zero)
        {
            yield return new SettingError(
                [IntervalKey],
                "settings.invalid_value",
                $"Retention:Decision:{nameof(Interval)} must be a positive duration (configured: {candidate.Interval}).");
        }
    }
}
