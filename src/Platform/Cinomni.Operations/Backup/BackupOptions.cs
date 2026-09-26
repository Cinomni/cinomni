using Cinomni.Operations.Settings;

namespace Cinomni.Operations.Backup;

/// <summary>
/// Where database backups are written, how many are kept, how often one is taken, and which external
/// tools produce and consume them.
/// <para>
/// A backup is the one artefact that must survive the installation it came from, so nothing here is
/// derived at run time from something that could have drifted: the root is canonicalised and frozen at
/// composition, and <see cref="Interval"/> becomes a scheduled-job cadence exactly like every retention
/// window. An inconsistent section therefore stops the Host at startup with a message naming the key,
/// rather than producing an unusable dump at three in the morning.
/// </para>
/// </summary>
public sealed class BackupOptions
{
    /// <summary>
    /// Whether the periodic <c>operations.backup</c> job is registered. Turning it off leaves the
    /// command, the store and the CLI verbs in place — a backup can still be taken by hand — it only
    /// removes the schedule.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Directory the dump and its manifest are written to. It must be outside every media root: a dump
    /// carries webhook capability URLs, indexer base URLs and password verifiers, and putting it inside
    /// a tree the library and playback surfaces read from would be a credential store in the open.
    /// <para>
    /// <see cref="Validate"/> replaces this with its canonical absolute form, so everything downstream
    /// compares and logs one spelling of the path.
    /// </para>
    /// </summary>
    public string Root { get; set; } = "/data/backups";

    /// <summary>
    /// How many complete backup sets are kept. The oldest sets beyond this count are removed after a
    /// successful run — never before, so a failed run never costs the operator their last good dump.
    /// </summary>
    public int KeepCount { get; set; } = 7;

    /// <summary>How often the <c>operations.backup</c> job runs.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromDays(1);

    /// <summary>
    /// The <c>pg_dump</c> to launch. Must be at least the major version of the server it reads, which
    /// is why a packaged installation ships the client matching the database it is deployed with.
    /// </summary>
    public string PgDumpPath { get; set; } = "pg_dump";

    /// <summary>The <c>pg_restore</c> that consumes a dump. Used by the restore drill and the operator script.</summary>
    public string PgRestorePath { get; set; } = "pg_restore";

    /// <summary>
    /// How long a single dump or restore may run before the child process is killed. Generous on
    /// purpose: a large installation on slow storage must not have its backup truncated by a timeout.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromHours(2);

    /// <summary>
    /// Internal (not private) so <see cref="BackupSettingDefinitions"/> names the exact same string
    /// rather than a duplicated literal that could drift from it.
    /// </summary>
    internal const string KeepCountKey = "backup.keepCount";
    internal const string IntervalKey = "retention.backup.interval";

    /// <summary>
    /// Fails fast at composition time and canonicalises <see cref="Root"/>. Called by
    /// <c>AddBackup</c>, so a misconfigured section stops the Host at startup rather than at the first
    /// scheduled run.
    /// <para>
    /// Canonicalising <see cref="Root"/> stays here rather than moving into <see cref="Check"/>: it writes
    /// a new value back into the option, which is not a pure check, and <c>Backup:Root</c> is itself
    /// excluded from the settings store (a dump's storage location is a deployment fact, never
    /// browser-editable) so it never needs to run against a candidate merged view.
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">The configured values are not usable.</exception>
    public void Validate()
    {
        Root = BackupRoot.Canonicalize("Backup:Root", Root);

        foreach (var error in Check(this))
        {
            throw new InvalidOperationException(error.Message);
        }
    }

    /// <summary>
    /// The rules themselves, as pure data, for every field <see cref="Validate"/> checks other than
    /// <see cref="Root"/> (see <see cref="Validate"/> for why that one stays out) — see
    /// <see cref="Cinomni.Operations.Retention.RetentionOptions.Check"/>'s remarks for why this and
    /// <see cref="Validate"/> must never drift.
    /// </summary>
    public static IEnumerable<SettingError> Check(BackupOptions candidate)
    {
        if (candidate.KeepCount < 1)
        {
            yield return new SettingError(
                [KeepCountKey],
                "settings.invalid_value",
                $"Backup:{nameof(KeepCount)} must keep at least one backup (configured: {candidate.KeepCount}).");
        }

        if (candidate.Interval <= TimeSpan.Zero)
        {
            yield return Positive(IntervalKey, nameof(Interval), candidate.Interval);
        }

        if (candidate.Timeout <= TimeSpan.Zero)
        {
            // Timeout has no settings key: it is not in the editable catalogue, but the rule stays pure
            // data here so Validate() has nothing left to duplicate.
            yield return new SettingError(
                [],
                "settings.invalid_value",
                $"Backup:{nameof(Timeout)} must be a positive duration (configured: {candidate.Timeout}).");
        }

        if (string.IsNullOrWhiteSpace(candidate.PgDumpPath))
        {
            yield return NotBlank(nameof(PgDumpPath));
        }

        if (string.IsNullOrWhiteSpace(candidate.PgRestorePath))
        {
            yield return NotBlank(nameof(PgRestorePath));
        }
    }

    private static SettingError Positive(string settingKey, string propertyName, TimeSpan value) =>
        new([settingKey], "settings.invalid_value", $"Backup:{propertyName} must be a positive duration (configured: {value}).");

    private static SettingError NotBlank(string propertyName) =>
        new([], "settings.invalid_value", $"Backup:{propertyName} must name an executable; it is not set.");
}
