using Cinomni.Operations.Settings;

namespace Cinomni.Operations.Retention;

/// <summary>
/// Retention windows for the tables the platform kernel itself owns (the outbox and the command
/// queue), plus the shared purge cadence and batch size every module purge inherits.
/// <para>
/// A module keeps its own options type for its own tables; only what lives in the
/// <c>operations</c> schema is configured here, so the platform stays free of module concepts.
/// </para>
/// </summary>
public sealed class RetentionOptions
{
    /// <summary>
    /// How long a <b>published</b> outbox message is kept. An unpublished message is never purged
    /// at any age — it is undelivered work, not history.
    /// </summary>
    public TimeSpan OutboxRetention { get; set; } = TimeSpan.FromDays(14);

    /// <summary>
    /// How long a <c>Completed</c> command is kept.
    /// <para>
    /// <b>Invariant: strictly greater than <see cref="OutboxRetention"/>.</b> A command row is also
    /// its idempotency record: deleting it frees the <c>ux_command_idempotency_key</c> slot, so a
    /// message that is published late would enqueue — and re-execute — a command that already ran.
    /// Keeping this window longer than the outbox window guarantees the dedup row still exists for
    /// every message that can still be published. <see cref="Validate"/> enforces it at startup.
    /// </para>
    /// </summary>
    public TimeSpan CompletedCommandRetention { get; set; } = TimeSpan.FromDays(30);

    /// <summary>
    /// How long a <c>Failed</c> command is kept — much longer than a completed one, because it is
    /// the operator's evidence of what went wrong. Also bound by the invariant above.
    /// </summary>
    public TimeSpan FailedCommandRetention { get; set; } = TimeSpan.FromDays(180);

    /// <summary>
    /// Rows deleted per statement. Every purge deletes in bounded batches so no single statement
    /// holds a long lock on a table the rest of the installation is writing to.
    /// </summary>
    public int BatchSize { get; set; } = 5_000;

    /// <summary>How often the <c>operations.retention</c> job runs.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromDays(1);

    /// <summary>
    /// Dotted setting keys, kept next to the rules that name them in a <see cref="SettingError"/>.
    /// Internal (not private) so <see cref="RetentionSettingDefinitions"/> names the exact same string
    /// rather than a duplicated literal that could drift from it.
    /// </summary>
    internal const string OutboxRetentionKey = "retention.operations.outboxRetention";
    internal const string CompletedCommandRetentionKey = "retention.operations.completedCommandRetention";
    internal const string FailedCommandRetentionKey = "retention.operations.failedCommandRetention";
    internal const string IntervalKey = "retention.operations.interval";
    internal const string BatchSizeKey = "retention.operations.batchSize";

    /// <summary>
    /// Fails fast at composition time on a configuration that would make the purge unsafe. Called by
    /// <c>AddOperations</c>, so a bad <c>Retention</c> section stops the Host at startup with a
    /// message naming the key rather than silently re-running finished commands weeks later.
    /// <para>
    /// The throwing wrapper over <see cref="Check"/>: same rules, same order, same messages — see
    /// <see cref="Check"/> for why the two must never drift.
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">The configured windows are not self-consistent.</exception>
    public void Validate()
    {
        foreach (var error in Check(this))
        {
            throw new InvalidOperationException(error.Message);
        }
    }

    /// <summary>
    /// The rules themselves, as pure data. <see cref="Validate"/> throws on the first one <see cref="Check"/>
    /// yields; the settings write path instead collects every one against the candidate merged view — the
    /// installation's state with a batch of pending changes applied — so a rejection names every key the
    /// rule depends on rather than the one that happened to be edited.
    /// <para>
    /// ONE implementation, two shapes, deliberately: if <see cref="Validate"/> and this ever diverged, a
    /// value the settings panel accepted could stop the next startup — the worst outcome this store can
    /// produce. Nothing here throws; there is nothing left in <see cref="Validate"/> to drift out of sync
    /// with.
    /// </para>
    /// </summary>
    public static IEnumerable<SettingError> Check(RetentionOptions candidate)
    {
        if (candidate.OutboxRetention <= TimeSpan.Zero)
        {
            yield return Positive(OutboxRetentionKey, nameof(OutboxRetention), candidate.OutboxRetention);
        }

        if (candidate.CompletedCommandRetention <= TimeSpan.Zero)
        {
            yield return Positive(
                CompletedCommandRetentionKey, nameof(CompletedCommandRetention), candidate.CompletedCommandRetention);
        }

        if (candidate.FailedCommandRetention <= TimeSpan.Zero)
        {
            yield return Positive(FailedCommandRetentionKey, nameof(FailedCommandRetention), candidate.FailedCommandRetention);
        }

        if (candidate.Interval <= TimeSpan.Zero)
        {
            yield return Positive(IntervalKey, nameof(Interval), candidate.Interval);
        }

        if (candidate.BatchSize <= 0)
        {
            yield return new SettingError(
                [BatchSizeKey],
                "settings.invalid_value",
                $"Retention:{nameof(BatchSize)} must be greater than zero (configured: {candidate.BatchSize}).");
        }

        if (candidate.CompletedCommandRetention <= candidate.OutboxRetention)
        {
            yield return new SettingError(
                [CompletedCommandRetentionKey, OutboxRetentionKey],
                "settings.retention_window_too_short",
                $"Retention:{nameof(CompletedCommandRetention)} ({candidate.CompletedCommandRetention}) must be strictly "
                + $"greater than Retention:{nameof(OutboxRetention)} ({candidate.OutboxRetention}). A completed command row "
                + "is its own idempotency record: purging it before the outbox message that produced it would let "
                + "a late publication re-execute work that already ran.");
        }

        if (candidate.FailedCommandRetention <= candidate.OutboxRetention)
        {
            yield return new SettingError(
                [FailedCommandRetentionKey, OutboxRetentionKey],
                "settings.retention_window_too_short",
                $"Retention:{nameof(FailedCommandRetention)} ({candidate.FailedCommandRetention}) must be strictly greater "
                + $"than Retention:{nameof(OutboxRetention)} ({candidate.OutboxRetention}), for the same idempotency reason "
                + $"as Retention:{nameof(CompletedCommandRetention)}.");
        }
    }

    private static SettingError Positive(string settingKey, string propertyName, TimeSpan value) =>
        new(
            [settingKey],
            "settings.invalid_value",
            $"Retention:{propertyName} must be a positive duration (configured: {value}).");
}
