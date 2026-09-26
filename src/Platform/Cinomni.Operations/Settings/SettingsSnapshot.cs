using System.Collections.Immutable;

namespace Cinomni.Operations.Settings;

/// <summary>
/// One immutable, versioned view of every stored setting value, keyed by <see cref="SettingDefinition.Key"/>.
/// Published as a whole so a reader never observes a half-updated set of overrides; the generation is
/// what lets <see cref="LiveOptions{TOptions}"/> tell "nothing changed" from "rebuild" with a single
/// integer comparison instead of a dictionary diff.
/// <para>
/// A key present here means an override is in effect, decrypted where the row is a secret. Secrets are
/// decrypted once, when the snapshot is built, and held in this dictionary in process memory — they
/// must be, to be usable at read time. A secret row that cannot be decrypted (no master key loaded, a
/// lost/rotated key, or a tampered row) is treated as absent rather than thrown on, so a database that
/// already has one does not stop the process from starting.
/// </para>
/// </summary>
public sealed record SettingsSnapshot(long Generation, ImmutableDictionary<string, string> Values)
{
    /// <summary>The snapshot before anything has ever been loaded: generation zero, no overrides.</summary>
    public static readonly SettingsSnapshot Empty = new(0, ImmutableDictionary<string, string>.Empty);
}
