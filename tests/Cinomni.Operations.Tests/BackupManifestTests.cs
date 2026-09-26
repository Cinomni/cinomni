using Cinomni.Operations.Backup;
using Xunit;

namespace Cinomni.Operations.Tests;

/// <summary>
/// The manifest is the one artefact of a backup an operator will paste into a bug report, so what it
/// carries — and what it must never carry — is a contract, not an implementation detail.
/// </summary>
public sealed class BackupManifestTests
{
    private static BackupManifest Sample() => new()
    {
        CreatedAt = new DateTimeOffset(2026, 7, 29, 3, 0, 0, TimeSpan.Zero),
        DatabaseName = "cinomni",
        ServerVersion = "16.4",
        ToolVersion = "pg_dump (PostgreSQL) 16.4",
        DumpFileName = "cinomni-20260729T030000Z.dump",
        DumpSizeBytes = 1_234_567,
        DumpSha256 = "6b86b273ff34fce19d6b804eff5a3f5747ada4eaa22f1d49c01e52ddb7875b4b",
        Schemas =
        [
            new BackupSchemaState("operations", ["20260723220629_InitialOperations"]),
            new BackupSchemaState("identity", ["20260723230000_InitialIdentity"]),
        ],
    };

    [Fact]
    public void A_manifest_round_trips_through_json_without_losing_the_schema_state()
    {
        var original = Sample();

        var restored = BackupManifest.TryParse(original.ToJson());

        Assert.NotNull(restored);
        Assert.Equal(BackupManifest.CurrentFormatVersion, restored.FormatVersion);
        Assert.Equal(original.CreatedAt, restored.CreatedAt);
        Assert.Equal(original.DumpSha256, restored.DumpSha256);
        Assert.Equal(original.DumpSizeBytes, restored.DumpSizeBytes);
        Assert.Equal(2, restored.Schemas.Count);
        Assert.Equal("operations", restored.Schemas[0].Name);
        Assert.Equal(["20260723220629_InitialOperations"], restored.Schemas[0].AppliedMigrations);
    }

    /// <summary>
    /// The manifest must stay safe to share. A dump is a credential store; its description must not be
    /// one too, or the safe half of the pair stops being safe.
    /// </summary>
    [Fact]
    public void A_manifest_carries_no_way_of_reaching_the_database()
    {
        var json = Sample().ToJson();

        foreach (var forbidden in new[] { "password", "username", "host", "port", "connectionstring" })
        {
            Assert.DoesNotContain(forbidden, json, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void A_document_that_is_not_a_manifest_is_read_as_absent_rather_than_throwing()
    {
        Assert.Null(BackupManifest.TryParse(string.Empty));
        Assert.Null(BackupManifest.TryParse("   "));
        Assert.Null(BackupManifest.TryParse("{ this is not json"));
        Assert.Null(BackupManifest.TryParse("[1, 2, 3]"));
    }

    /// <summary>
    /// A hand-edited manifest is how an operator would try to force an incompatible restore through, so
    /// the edit has to survive parsing and be caught by the compatibility gate instead of here.
    /// </summary>
    [Fact]
    public void A_hand_edited_migration_list_parses_so_the_compatibility_gate_is_what_refuses_it()
    {
        var tampered = Sample().ToJson().Replace(
            "20260723220629_InitialOperations",
            "29990101000000_FromTheFuture",
            StringComparison.Ordinal);

        var parsed = BackupManifest.TryParse(tampered);

        Assert.NotNull(parsed);
        Assert.Equal(["29990101000000_FromTheFuture"], parsed.Schemas[0].AppliedMigrations);
    }
}
