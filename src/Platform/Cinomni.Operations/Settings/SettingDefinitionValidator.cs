using System.Globalization;
using System.Text.RegularExpressions;

namespace Cinomni.Operations.Settings;

/// <summary>
/// Gate (a) of the write-path validation pipeline: the per-key kind/range/regex/enum rules carried as
/// data on <see cref="SettingDefinition.Validation"/>. Runs before any candidate merged view is built, so
/// a malformed single value never reaches the cross-key gate or the trial bind.
/// <para>
/// Deliberately knows nothing about any module's options type — only the wire primitives a
/// <see cref="SettingDefinition"/> already carries — matching the same "no module concepts in the
/// platform kernel" rule <see cref="SettingDefinition"/> itself documents.
/// </para>
/// </summary>
public static class SettingDefinitionValidator
{
    /// <summary>Clearing a key always falls back to a lower-precedence value that already validated when
    /// it was written (a file default or a code default), so a <see cref="SettingChangeKind.Clear"/>
    /// change has nothing here to check.</summary>
    public static IEnumerable<SettingError> Validate(SettingDefinition definition, SettingChange change)
    {
        if (change.Kind == SettingChangeKind.Clear)
        {
            yield break;
        }

        var rules = definition.Validation ?? new SettingValidation();
        var value = change.Value ?? string.Empty;

        if (rules.Required && value.Length == 0)
        {
            yield return Error(definition, "settings.value_required", "a value is required.", value);
            yield break;
        }

        // An empty, non-required value is the documented "disabled"/"unset" degradation for several
        // kinds (an empty secret, an unset optional text) — nothing further to check against it.
        if (value.Length == 0)
        {
            yield break;
        }

        foreach (var error in definition.Kind switch
        {
            SettingKind.Text or SettingKind.Secret => ValidateScalarText(definition, value, rules),
            SettingKind.Number => ValidateNumber(definition, value, rules),
            SettingKind.Boolean => ValidateBoolean(definition, value),
            SettingKind.Duration => ValidateDuration(definition, value, rules),
            SettingKind.List => ValidateList(definition, value, rules),
            SettingKind.Enum => ValidateEnum(definition, value, rules),
            _ => [],
        })
        {
            yield return error;
        }
    }

    private static IEnumerable<SettingError> ValidateScalarText(
        SettingDefinition definition, string value, SettingValidation rules)
    {
        foreach (var error in CheckLength(definition, value, rules))
        {
            yield return error;
        }

        if (rules.Pattern is { } pattern && !SafeMatch(pattern, value))
        {
            yield return Error(definition, "settings.pattern_mismatch", "does not match the expected format.", value);
        }
    }

    private static IEnumerable<SettingError> ValidateNumber(
        SettingDefinition definition, string value, SettingValidation rules)
    {
        // Bound the raw string first: a zero-padded numeric literal hundreds of characters long still
        // parses to an in-range double, and would otherwise reach the audit table's bounded column.
        foreach (var error in CheckLength(definition, value, rules))
        {
            yield return error;
        }

        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            yield return Error(definition, "settings.invalid_number", "must be a number.", value);
            yield break;
        }

        // A number-kind definition's Pattern (when set) narrows the format this gate accepts beyond
        // "any double" — e.g. digits only — so it stays exactly as parseable downstream by an
        // integer-only binder as it was here.
        if (rules.Pattern is { } pattern && !SafeMatch(pattern, value))
        {
            yield return Error(definition, "settings.pattern_mismatch", "does not match the expected format.", value);
        }

        foreach (var error in CheckRange(definition, value, parsed, rules))
        {
            yield return error;
        }
    }

    private static IEnumerable<SettingError> ValidateBoolean(SettingDefinition definition, string value)
    {
        if (!bool.TryParse(value, out _))
        {
            yield return Error(definition, "settings.invalid_boolean", "must be true or false.", value);
        }
    }

    private static IEnumerable<SettingError> ValidateDuration(
        SettingDefinition definition, string value, SettingValidation rules)
    {
        // Bound the raw string first, for the same reason as ValidateNumber above.
        foreach (var error in CheckLength(definition, value, rules))
        {
            yield return error;
        }

        if (!TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var parsed))
        {
            yield return Error(definition, "settings.invalid_duration", "must be a duration.", value);
            yield break;
        }

        // MinValue/MaxValue are expressed in seconds for a Duration-kind definition. A definition without
        // an explicit ceiling here would let a value near TimeSpan.MaxValue through this parse — it is a
        // legal TimeSpan — only to throw later in ordinary arithmetic against DateTimeOffset.UtcNow.
        foreach (var error in CheckRange(definition, value, parsed.TotalSeconds, rules))
        {
            yield return error;
        }
    }

    private static IEnumerable<SettingError> ValidateEnum(
        SettingDefinition definition, string value, SettingValidation rules)
    {
        if (rules.AllowedValues is not { Count: > 0 } allowed || !allowed.Contains(value, StringComparer.Ordinal))
        {
            yield return Error(definition, "settings.not_a_member", "is not one of the allowed values.", value);
        }
    }

    /// <summary>
    /// A comma-separated list, matching the storage convention <see cref="SettingsView"/> already reads.
    /// <see cref="SettingValidation.MinLength"/>/<see cref="SettingValidation.MaxLength"/> bound the entry
    /// COUNT for this kind (not a string length); <see cref="SettingValidation.Pattern"/>, when set, must
    /// match every entry; <see cref="SettingValidation.AllowedValues"/>, when set, is the closed set every
    /// entry must belong to. Duplicate entries are always rejected.
    /// </summary>
    private static IEnumerable<SettingError> ValidateList(
        SettingDefinition definition, string value, SettingValidation rules)
    {
        var entries = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (rules.MinLength is { } min && entries.Length < min)
        {
            yield return Error(definition, "settings.too_few_entries", $"must have at least {min} entr{Plural(min)}.", value);
        }

        if (rules.MaxLength is { } max && entries.Length > max)
        {
            yield return Error(definition, "settings.too_many_entries", $"must have at most {max} entr{Plural(max)}.", value);
        }

        if (entries.Distinct(StringComparer.Ordinal).Count() != entries.Length)
        {
            yield return Error(definition, "settings.duplicate_entry", "must not repeat an entry.", value);
        }

        foreach (var entry in entries)
        {
            if (rules.Pattern is { } pattern && !SafeMatch(pattern, entry))
            {
                yield return Error(definition, "settings.pattern_mismatch", $"entry '{entry}' does not match the expected format.", value);
            }

            if (rules.AllowedValues is { Count: > 0 } allowed && !allowed.Contains(entry, StringComparer.Ordinal))
            {
                yield return Error(definition, "settings.not_a_member", $"entry '{entry}' is not one of the allowed values.", value);
            }
        }
    }

    private static IEnumerable<SettingError> CheckLength(SettingDefinition definition, string value, SettingValidation rules)
    {
        if (rules.MinLength is { } min && value.Length < min)
        {
            yield return Error(definition, "settings.too_short", $"must be at least {min} characters.", value);
        }

        if (rules.MaxLength is { } max && value.Length > max)
        {
            yield return Error(definition, "settings.too_long", $"must be at most {max} characters.", value);
        }
    }

    private static IEnumerable<SettingError> CheckRange(
        SettingDefinition definition, string value, double parsed, SettingValidation rules)
    {
        if (rules.MinValue is { } min && parsed < min)
        {
            yield return Error(definition, "settings.below_minimum", $"must be at least {min}.", value);
        }

        if (rules.MaxValue is { } max && parsed > max)
        {
            yield return Error(definition, "settings.above_maximum", $"must be at most {max}.", value);
        }
    }

    private static SettingError Error(SettingDefinition definition, string code, string rule, string offendingValue)
    {
        var descriptor = definition.IsSecret ? "(value withheld)" : $"(configured: '{offendingValue}')";
        return new SettingError([definition.Key], code, $"'{definition.Key}' {rule} {descriptor}");
    }

    private static string Plural(int count) => count == 1 ? "y" : "ies";

    /// <summary>
    /// A definition's <see cref="SettingValidation.Pattern"/> is code-authored, not attacker-supplied, but
    /// still bounded against a runaway match on a long input rather than trusted unconditionally.
    /// </summary>
    private static bool SafeMatch(string pattern, string candidate)
    {
        try
        {
            return Regex.IsMatch(candidate, pattern, RegexOptions.None, TimeSpan.FromMilliseconds(200));
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
