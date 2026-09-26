namespace Cinomni.Operations.Settings;

/// <summary>
/// A cheap stand-in for "did <c>operations.setting</c> change". Comparing this triple is far cheaper
/// than reloading and diffing the whole table on every poll tick, and is exact for the write shapes
/// this store makes: a write always changes the row count, bumps <c>version</c>, or advances
/// <c>updated_at</c>.
/// </summary>
public sealed record SettingsFingerprint(int Count, long? MaxVersion, DateTimeOffset? MaxUpdatedAt)
{
    public static readonly SettingsFingerprint Empty = new(0, null, null);
}
