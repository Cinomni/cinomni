namespace Cinomni.Operations.Settings;

/// <summary>
/// The single in-process source of truth for stored setting overrides. Holds one
/// <see cref="SettingsSnapshot"/> behind a volatile field: a reader takes an interlocked read with no
/// lock and no database round trip, which is what makes "cheap read at use time" an honest claim for
/// <see cref="ILiveOptions{TOptions}"/>.
/// <para>
/// Registered as a singleton. The startup loader and the 30-second drift poll are the only writers in
/// this increment. The write path (a later increment) publishes here too — always after its
/// transaction commits, never before, so a rejected value never reaches this cache.
/// </para>
/// </summary>
public sealed class SettingsCache
{
    private SettingsSnapshot _snapshot = SettingsSnapshot.Empty;

    /// <summary>The current snapshot. Never blocks and never touches the database.</summary>
    public SettingsSnapshot Current => Volatile.Read(ref _snapshot);

    /// <summary>Publishes a new snapshot as one atomic swap. Callers own generation numbering.</summary>
    public void Publish(SettingsSnapshot snapshot) => Volatile.Write(ref _snapshot, snapshot);
}
