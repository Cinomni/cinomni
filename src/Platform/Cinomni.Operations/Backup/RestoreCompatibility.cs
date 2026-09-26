using System.Globalization;

namespace Cinomni.Operations.Backup;

/// <summary>
/// The verdict on restoring one dump into one build. <see cref="IsCompatible"/> is the only thing a
/// script needs; the rest exists so a refusal names what is wrong instead of just failing.
/// </summary>
/// <param name="IsCompatible">Whether this build can safely be run against the restored database.</param>
/// <param name="RefusalCode">Stable code of the refusal, or <c>null</c> when compatible.</param>
/// <param name="Summary">One line an operator can act on.</param>
/// <param name="Details">
/// Supporting lines: what the refusal found, or — on a compatible dump — the migrations this build will
/// apply on its first start after the restore.
/// </param>
public sealed record RestoreCompatibilityReport(
    bool IsCompatible,
    string? RefusalCode,
    string Summary,
    IReadOnlyList<string> Details);

/// <summary>
/// Decides whether a dump may be restored into the database this build is about to run against.
/// <para>
/// The rule that matters is one-directional. A dump whose schema state this build <b>already contains</b>
/// is fine: the Host migrates forward on startup, which is what every upgrade does anyway. A dump that
/// carries a migration this build does not know came from a <b>newer</b> version, and restoring it would
/// leave running code with tables it has no model for and no migration path back — the failure mode
/// DEPLOYMENT.md already warns about for image rollbacks, except silent.
/// </para>
/// <para>
/// "This build does not know it" is asked twice, because the installation keeps one migration history
/// for every module: once per schema, against the context that owns it, and once over
/// <see cref="BackupManifest.UnrecognizedMigrations"/>, which catches a migration belonging to a module
/// this build does not have at all — a schema whose name never appears in the manifest cannot be caught
/// by the per-schema rule.
/// </para>
/// <para>
/// This is pure: no database, no filesystem. The dump's integrity is a separate question, answered by
/// <see cref="BackupService.VerifyAsync"/> against the manifest's hash.
/// </para>
/// </summary>
public static class RestoreCompatibility
{
    /// <param name="manifest">The manifest written beside the dump.</param>
    /// <param name="knownMigrationsBySchema">The migrations this build carries, by schema.</param>
    /// <param name="targetServerVersion">
    /// The PostgreSQL version of the server being restored into, or <c>null</c> when it could not be
    /// read — in which case the server rule is skipped and said so, rather than guessed at.
    /// </param>
    public static RestoreCompatibilityReport Check(
        BackupManifest manifest,
        IReadOnlyDictionary<string, IReadOnlyList<string>> knownMigrationsBySchema,
        string? targetServerVersion)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(knownMigrationsBySchema);

        if (manifest.FormatVersion != BackupManifest.CurrentFormatVersion)
        {
            return Refuse(
                "backup.unsupported_format",
                $"The manifest is format version {manifest.FormatVersion}; this build reads version "
                + $"{BackupManifest.CurrentFormatVersion}.",
                ["A manifest from a newer build describes an archive this build cannot reason about."]);
        }

        var serverVerdict = CheckServerVersion(manifest.ServerVersion, targetServerVersion);
        if (serverVerdict is not null)
        {
            return serverVerdict;
        }

        var unknownSchemas = manifest.Schemas
            .Select(schema => schema.Name)
            .Where(name => !knownMigrationsBySchema.ContainsKey(name))
            .ToArray();

        if (unknownSchemas.Length > 0)
        {
            return Refuse(
                "backup.unknown_schema",
                "The backup contains schemas this build does not own.",
                [.. unknownSchemas.Select(name => $"schema '{name}' is in the backup and not in this build")]);
        }

        // The installation records every module's migrations in one history table, so an id nobody in
        // this build claims is the clearest possible evidence that the dump outran the binary.
        if (manifest.UnrecognizedMigrations.Count > 0)
        {
            return Refuse(
                "backup.dump_from_newer_build",
                "The backup carries migrations no module in this build owns.",
                [
                    .. manifest.UnrecognizedMigrations.Select(
                        migration => $"migration '{migration}' was applied and no module here carries it"),
                    "Restore it into the version that produced it, or upgrade first.",
                ]);
        }

        var newerMigrations = new List<string>();
        foreach (var schema in manifest.Schemas)
        {
            var known = knownMigrationsBySchema[schema.Name];
            newerMigrations.AddRange(schema.AppliedMigrations
                .Where(migration => !known.Contains(migration, StringComparer.Ordinal))
                .Select(migration => $"schema '{schema.Name}' has migration '{migration}' applied, which this build does not carry"));
        }

        if (newerMigrations.Count > 0)
        {
            return Refuse(
                "backup.dump_from_newer_build",
                "The backup was taken by a newer build of Cinomni than the one being restored into.",
                [
                    .. newerMigrations,
                    "Restore it into the version that produced it, or upgrade first. Running an older "
                    + "build against a newer schema is not supported and cannot be undone by migrating.",
                ]);
        }

        var details = new List<string>();

        var byName = manifest.Schemas.ToDictionary(schema => schema.Name, StringComparer.Ordinal);
        foreach (var (schema, known) in knownMigrationsBySchema.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            if (!byName.TryGetValue(schema, out var state))
            {
                details.Add($"schema '{schema}' is new in this build; all {known.Count} of its migrations will be applied on startup");
                continue;
            }

            var pending = known.Except(state.AppliedMigrations, StringComparer.Ordinal).ToArray();
            if (pending.Length > 0)
            {
                details.Add($"schema '{schema}': {pending.Length} migration(s) will be applied on startup — {string.Join(", ", pending)}");
            }
        }

        var summary = details.Count == 0
            ? "The backup matches this build exactly; nothing will be migrated on the next start."
            : "The backup is older than this build. The Host will migrate it forward on its next start.";

        return new RestoreCompatibilityReport(true, null, summary, details);
    }

    private static RestoreCompatibilityReport? CheckServerVersion(string dumpVersion, string? targetVersion)
    {
        if (targetVersion is null)
        {
            return null;
        }

        var dumpMajor = MajorVersion(dumpVersion);
        var targetMajor = MajorVersion(targetVersion);

        if (dumpMajor is null || targetMajor is null || targetMajor >= dumpMajor)
        {
            return null;
        }

        return Refuse(
            "backup.older_server",
            $"The backup was taken from PostgreSQL {dumpMajor} and the target server is {targetMajor}.",
            ["A dump cannot be restored into an older major version of PostgreSQL."]);
    }

    /// <summary>Reads the leading major number out of a version string such as <c>16.4</c> or <c>160004</c>.</summary>
    private static int? MajorVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var digits = version.TakeWhile(char.IsAsciiDigit).ToArray();
        return digits.Length > 0 && int.TryParse(digits, CultureInfo.InvariantCulture, out var major) ? major : null;
    }

    private static RestoreCompatibilityReport Refuse(string code, string summary, IReadOnlyList<string> details) =>
        new(false, code, summary, details);
}
