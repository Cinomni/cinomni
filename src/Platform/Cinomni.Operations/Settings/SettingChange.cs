namespace Cinomni.Operations.Settings;

/// <summary>Whether a <see cref="SettingChange"/> writes a new value or resets the key to its default.</summary>
public enum SettingChangeKind
{
    /// <summary>Store <see cref="SettingChange.Value"/> as the new override.</summary>
    Set,

    /// <summary>Remove the stored override; the key falls back to its file value or code default.</summary>
    Clear,
}

/// <summary>
/// One requested change in a batch passed to <see cref="ISettingsAdministration.ApplyAsync"/>.
/// <see cref="ExpectedVersion"/> is the row version the caller last saw — <c>0</c> when the caller
/// believes no row exists yet — and is the optimistic-concurrency check the write path enforces per key.
/// </summary>
public sealed record SettingChange(string Key, SettingChangeKind Kind, string? Value, long ExpectedVersion)
{
    public static SettingChange Set(string key, string value, long expectedVersion) =>
        new(key, SettingChangeKind.Set, value, expectedVersion);

    public static SettingChange Clear(string key, long expectedVersion) =>
        new(key, SettingChangeKind.Clear, null, expectedVersion);
}
