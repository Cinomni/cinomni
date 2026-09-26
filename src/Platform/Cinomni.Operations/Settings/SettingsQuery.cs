using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Cinomni.Operations.Persistence;

namespace Cinomni.Operations.Settings;

/// <summary>
/// The read-only projection behind <c>GET /api/operations/settings</c>: every registered
/// <see cref="SettingDefinition"/>, its currently effective value, and where that value comes from.
/// <para>
/// A definition's provenance is resolved with the same precedence <see cref="SettingsView.GetRawValue"/>
/// already walks (environment/command-line pin, then a stored row, then a configuration file value,
/// then the definition's own default), reported as a label instead of merely applied. Because
/// <see cref="SettingsSnapshot"/> carries no per-key version, this hits <c>operations.setting</c>
/// directly for <see cref="SettingSummary.Version"/> and <see cref="SettingSummary.IsSet"/> rather than
/// reading them off the in-process cache.
/// </para>
/// </summary>
public sealed class SettingsQuery(
    OperationsDbContext dbContext,
    SettingsCache cache,
    IConfiguration configuration,
    IEnumerable<SettingDefinition> definitions)
{
    /// <summary>Every settable key in the build's catalogue, ordered by key for a stable listing.</summary>
    public async Task<IReadOnlyList<SettingSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        var versions = await ReadVersionsAsync(cancellationToken);
        var view = new SettingsView(cache.Current, configuration);

        return definitions
            .Select(definition => Project(definition, view, versions))
            .OrderBy(summary => summary.Key, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// One key's current projection, or <c>null</c> when no <see cref="SettingDefinition"/> names it —
    /// the write path's own answer for an unknown key, echoed here for a caller re-reading after a write.
    /// </summary>
    public async Task<SettingSummary?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var definition = definitions.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.Ordinal));
        if (definition is null)
        {
            return null;
        }

        var versions = await ReadVersionsAsync(cancellationToken, key);
        var view = new SettingsView(cache.Current, configuration);
        return Project(definition, view, versions);
    }

    private async Task<IReadOnlyDictionary<string, long>> ReadVersionsAsync(
        CancellationToken cancellationToken, string? onlyKey = null)
    {
        var query = dbContext.Settings.AsNoTracking();
        if (onlyKey is not null)
        {
            query = query.Where(s => s.Key == onlyKey);
        }

        return await query
            .Select(s => new { s.Key, s.Version })
            .ToDictionaryAsync(s => s.Key, s => s.Version, StringComparer.Ordinal, cancellationToken);
    }

    private SettingSummary Project(
        SettingDefinition definition, SettingsView view, IReadOnlyDictionary<string, long> versions)
    {
        var isSet = versions.TryGetValue(definition.Key, out var version);

        return new SettingSummary(
            definition.Key,
            definition.Kind,
            definition.IsSecret,
            definition.ConfigurationPath,
            // A secret value is never echoed over HTTP, even redacted-looking cipher text (SECURITY.md).
            definition.IsSecret ? null : view.GetRawValue(definition),
            isSet,
            ResolveSource(definition, isSet),
            isSet ? version : 0,
            definition.DefaultAsString,
            definition.Validation?.AllowedValues,
            definition.Validation?.MinValue,
            definition.Validation?.MaxValue);
    }

    private string ResolveSource(SettingDefinition definition, bool isSet)
    {
        if (SettingsPrecedence.IsPinned(configuration, definition.ConfigurationPath))
        {
            return SettingSource.Environment;
        }

        if (isSet)
        {
            return SettingSource.Database;
        }

        return string.IsNullOrWhiteSpace(configuration[definition.ConfigurationPath])
            ? SettingSource.Default
            : SettingSource.File;
    }
}

/// <summary>Where a setting's currently effective value comes from, matching the precedence chain.</summary>
public static class SettingSource
{
    public const string Environment = "environment";
    public const string Database = "database";
    public const string File = "file";
    public const string Default = "default";
}

/// <summary>
/// One settable key's current projection: what it is, what is currently in effect, and where that value
/// comes from. <see cref="Value"/> is always <c>null</c> for a secret-kind key regardless of whether one
/// is stored — <see cref="IsSet"/> is the only signal a caller gets for that case.
/// </summary>
/// <param name="Key">The dotted key, e.g. <c>retention.operations.batchSize</c>.</param>
/// <param name="Kind">The value's shape; decides how a caller should render and parse it.</param>
/// <param name="IsSecret">Whether <see cref="Value"/> is withheld by design.</param>
/// <param name="ConfigurationPath">The configuration path this key overrides when not in effect.</param>
/// <param name="Value">The effective raw value, or <c>null</c> when nothing supplies it or it is secret.</param>
/// <param name="IsSet">Whether a row exists in <c>operations.setting</c> for this key.</param>
/// <param name="Source">One of <see cref="SettingSource"/>.</param>
/// <param name="Version">
/// The stored row's optimistic-concurrency version, or <c>0</c> when <see cref="IsSet"/> is false — the
/// <c>expectedVersion</c> a caller must send back on the next write.
/// </param>
/// <param name="DefaultValue">The definition's own code default, as a string.</param>
/// <param name="AllowedValues">The closed set an enumeration takes, so a caller can offer it rather than guess.</param>
/// <param name="MinValue">The smallest number the key accepts, when bounded.</param>
/// <param name="MaxValue">The largest number the key accepts, when bounded.</param>
public sealed record SettingSummary(
    string Key,
    SettingKind Kind,
    bool IsSecret,
    string ConfigurationPath,
    string? Value,
    bool IsSet,
    string Source,
    long Version,
    string? DefaultValue,
    IReadOnlyList<string>? AllowedValues = null,
    double? MinValue = null,
    double? MaxValue = null);
