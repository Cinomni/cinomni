namespace Cinomni.Operations.Settings;

/// <summary>
/// The shape of a settable value. Closed and small: it decides which typed accessor on
/// <see cref="SettingsView"/> a binder uses, and — once the write path exists — how a display
/// formatter must treat a rejected input (SECURITY.md: a secret value must never be echoed back).
/// </summary>
public enum SettingKind
{
    Text,
    Secret,
    Number,
    Boolean,
    Duration,
    List,
    Enum,
}
