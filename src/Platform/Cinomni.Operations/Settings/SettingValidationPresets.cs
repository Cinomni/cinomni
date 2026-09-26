namespace Cinomni.Operations.Settings;

/// <summary>
/// Off-the-shelf <see cref="SettingValidation"/> shapes for the two kinds of key every retention window,
/// purge/backup cadence, and bounded count in the catalogue shares. Centralised so every module's
/// catalogue applies the same ceiling rather than repeating — and risking a drift in — the same magic
/// numbers.
/// <para>
/// The duration ceiling exists for a concrete reason: a value near <see cref="TimeSpan.MaxValue"/> passes
/// <c>TimeSpan.TryParse</c> (gate (a)'s own parser) but throws in ordinary arithmetic against
/// <see cref="DateTimeOffset.UtcNow"/> — exactly the arithmetic every purge handler and the backup
/// schedule perform. Rejecting it here, before it is ever persisted, is what keeps that arithmetic safe.
/// </para>
/// </summary>
public static class SettingValidationPresets
{
    /// <summary>
    /// A required duration between one minute and 365 days, at most 32 characters. 365 days already
    /// exceeds every default retention window this installation ships with, and stays far short of a
    /// value that could push purge or backup-schedule arithmetic toward <see cref="TimeSpan.MaxValue"/>.
    /// The length bound rejects a zero-padded numeric string long before it reaches the audit table's
    /// bounded column.
    /// </summary>
    public static readonly SettingValidation Duration = new(
        Required: true,
        MinValue: 60,
        MaxValue: 31_536_000,
        MaxLength: 32);

    /// <summary>
    /// A required positive integer count, digits only, at most 100,000 and 12 characters — comfortably
    /// above any batch size or backup-set count this installation would legitimately configure, and far
    /// short of a value that could overflow the arithmetic that consumes it.
    /// </summary>
    public static readonly SettingValidation Count = new(
        Required: true,
        MinValue: 1,
        MaxValue: 100_000,
        MaxLength: 12,
        Pattern: "^[0-9]+$");
}
