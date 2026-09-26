using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Cinomni.Operations.Settings;

/// <summary>
/// Resolves one <see cref="SettingDefinition"/> to its effective value under the fixed precedence
/// (highest first): an environment variable or command-line argument, a stored database override, a
/// configuration file value, then the definition's own code default.
/// <para>
/// A value pinned by the environment or the command line is returned even when a database row exists
/// for the same key — the stored row is then "stored, not in effect", never silently applied.
/// </para>
/// <para>
/// Built fresh only when the settings generation changes (see <see cref="LiveOptions{TOptions}"/>), so
/// the per-path pin check below is memoized for the lifetime of one instance rather than re-walking
/// the provider chain on every property a binder reads.
/// </para>
/// </summary>
public sealed class SettingsView
{
    private readonly SettingsSnapshot _snapshot;
    private readonly IConfiguration _configuration;
    private readonly Dictionary<string, bool> _pinned = new(StringComparer.Ordinal);

    public SettingsView(SettingsSnapshot snapshot, IConfiguration configuration)
    {
        _snapshot = snapshot;
        _configuration = configuration;
    }

    /// <summary>The raw effective string for a definition, or null when nothing supplies it at all.</summary>
    public string? GetRawValue(SettingDefinition definition)
    {
        if (!IsPinned(definition.ConfigurationPath)
            && _snapshot.Values.TryGetValue(definition.Key, out var stored))
        {
            return stored;
        }

        var configured = _configuration[definition.ConfigurationPath];
        return string.IsNullOrWhiteSpace(configured) ? definition.DefaultAsString : configured;
    }

    public string? GetString(SettingDefinition definition) => GetRawValue(definition);

    public bool? GetBool(SettingDefinition definition)
    {
        var raw = GetRawValue(definition);
        return raw is not null && bool.TryParse(raw, out var value) ? value : null;
    }

    public int? GetInt(SettingDefinition definition)
    {
        var raw = GetRawValue(definition);
        return raw is not null
            && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;
    }

    public long? GetLong(SettingDefinition definition)
    {
        var raw = GetRawValue(definition);
        return raw is not null
            && long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;
    }

    public TimeSpan? GetTimeSpan(SettingDefinition definition)
    {
        var raw = GetRawValue(definition);
        return raw is not null && TimeSpan.TryParse(raw, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    /// <summary>
    /// A list value. A stored database override is one comma-joined string (the write path's storage
    /// convention for a <see cref="SettingKind.List"/> row); a configuration-sourced value may instead
    /// be a JSON array section, matching how every list-typed option already binds today.
    /// </summary>
    public IReadOnlyList<string>? GetStringArray(SettingDefinition definition)
    {
        if (!IsPinned(definition.ConfigurationPath)
            && _snapshot.Values.TryGetValue(definition.Key, out var stored))
        {
            return SplitList(stored);
        }

        var section = _configuration.GetSection(definition.ConfigurationPath).Get<string[]>();
        if (section is { Length: > 0 })
        {
            return section;
        }

        return definition.DefaultAsString is { Length: > 0 } fallback ? SplitList(fallback) : null;
    }

    private static IReadOnlyList<string> SplitList(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private bool IsPinned(string configurationPath)
    {
        if (_pinned.TryGetValue(configurationPath, out var cached))
        {
            return cached;
        }

        var pinned = SettingsPrecedence.IsPinned(_configuration, configurationPath);
        _pinned[configurationPath] = pinned;
        return pinned;
    }
}
