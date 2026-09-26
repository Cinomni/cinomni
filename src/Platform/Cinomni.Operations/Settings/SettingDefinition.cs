namespace Cinomni.Operations.Settings;

/// <summary>
/// Describes one settable key: what kind of value it holds, whether it is a secret, and the
/// configuration path it overrides when a stored value is not in effect. Definitions live in code and
/// ship with the build — <c>operations.setting</c> stores overrides only, never the catalogue of what
/// is settable.
/// <para>
/// No module type appears here: Operations never learns what a module's options class looks like,
/// only the wire primitives needed to resolve and, later, validate one value (architecture rule: keep
/// the kernel free of module concepts).
/// </para>
/// </summary>
public sealed record SettingDefinition(
    string Key,
    SettingKind Kind,
    bool IsSecret,
    string ConfigurationPath,
    string? DefaultAsString = null,
    SettingValidation? Validation = null);
