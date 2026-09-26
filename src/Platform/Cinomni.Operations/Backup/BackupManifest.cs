using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cinomni.Operations.Backup;

/// <summary>The migrations one schema had applied at the instant the dump was taken.</summary>
/// <param name="Name">The PostgreSQL schema name, as its owning context declares it.</param>
/// <param name="AppliedMigrations">
/// Every migration id in that schema's <c>__EFMigrationsHistory</c>, in applied order. This is what
/// lets a restore refuse a dump the binary cannot read: an id this build does not know means the dump
/// came from a newer version, whose tables the current code has no model for.
/// </param>
public sealed record BackupSchemaState(string Name, IReadOnlyList<string> AppliedMigrations);

/// <summary>
/// The record written beside every dump. It answers the two questions a restore has to ask before it
/// touches anything: is this file intact (<see cref="DumpSha256"/>), and does the schema state it
/// carries match the binary being restored into (<see cref="Schemas"/>).
/// <para>
/// It deliberately carries <b>no connection detail</b> — no host, no port, no user, no password. The
/// manifest is the one artefact an operator will paste into a bug report, and it must stay safe to
/// paste. The dump beside it is the opposite and must be treated as a credential store.
/// </para>
/// </summary>
public sealed record BackupManifest
{
    /// <summary>The only manifest format this build writes and accepts.</summary>
    public const int CurrentFormatVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Format of this document. A restore refuses a version it does not know.</summary>
    public int FormatVersion { get; init; } = CurrentFormatVersion;

    /// <summary>When the dump was taken (UTC).</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>The database that was dumped. The name only — never how to reach it.</summary>
    public string DatabaseName { get; init; } = string.Empty;

    /// <summary>The PostgreSQL server version the dump was taken from, e.g. <c>16.4</c>.</summary>
    public string ServerVersion { get; init; } = string.Empty;

    /// <summary>The <c>pg_dump</c> build that produced the archive, as it reports itself.</summary>
    public string ToolVersion { get; init; } = string.Empty;

    /// <summary>File name of the dump this manifest describes. A name, never a path.</summary>
    public string DumpFileName { get; init; } = string.Empty;

    public long DumpSizeBytes { get; init; }

    /// <summary>Lower-case hexadecimal SHA-256 of the dump, so a truncated archive is caught before a restore.</summary>
    public string DumpSha256 { get; init; } = string.Empty;

    /// <summary>Every schema in the dump and the migrations it had applied.</summary>
    public IReadOnlyList<BackupSchemaState> Schemas { get; init; } = [];

    /// <summary>
    /// Applied migrations no schema in this manifest claims. Empty on a healthy installation. A value
    /// here means the installation that produced the dump ran code this build does not have — a module
    /// that has since been removed, or, far more likely, a newer version — and the restore refuses.
    /// </summary>
    public IReadOnlyList<string> UnrecognizedMigrations { get; init; } = [];

    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);

    /// <summary>
    /// Reads a manifest, returning <c>null</c> for anything that is not one. A malformed file is an
    /// expected outcome (a truncated write, a hand-edited document), not an exception.
    /// </summary>
    public static BackupManifest? TryParse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<BackupManifest>(json, SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
