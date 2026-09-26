namespace Cinomni.Operations.Settings;

/// <summary>
/// Validation rules for one <see cref="SettingDefinition"/>, carried as data rather than code so the
/// definition stays a wire-primitive record — no delegate, no reference to a module's options type.
/// <para>
/// Nothing in this increment evaluates these rules; the write-path validation pipeline
/// (per-key gate, before the candidate-merged-view and trial-bind gates) is what enforces them. The
/// field is here now because every later increment builds on the definition's shape being fixed.
/// </para>
/// </summary>
public sealed record SettingValidation(
    bool Required = false,
    int? MinLength = null,
    int? MaxLength = null,
    double? MinValue = null,
    double? MaxValue = null,
    string? Pattern = null,
    IReadOnlyList<string>? AllowedValues = null);
