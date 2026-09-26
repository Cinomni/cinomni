using Cinomni.Operations.Backup;
using Xunit;

namespace Cinomni.Operations.Tests;

/// <summary>
/// The gate that makes a manifest worth writing: a restore refuses a dump the binary being restored into
/// cannot read.
/// <para>
/// The rule is deliberately one-directional. Restoring an <b>older</b> dump is normal — the Host migrates
/// forward on startup, which is what every upgrade already does. Restoring a <b>newer</b> one leaves
/// running code with tables it has no model for and no way back, which is the silent version of the
/// rollback failure DEPLOYMENT.md warns about.
/// </para>
/// </summary>
public sealed class RestoreCompatibilityTests
{
    private static readonly string[] OperationsMigrations =
    [
        "20260723220629_InitialOperations",
        "20260723222008_CommandQueueAndScheduler",
    ];

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ThisBuild(
        params (string Schema, string[] Migrations)[] schemas) =>
        schemas.ToDictionary(
            entry => entry.Schema,
            entry => (IReadOnlyList<string>)entry.Migrations,
            StringComparer.Ordinal);

    private static BackupManifest Manifest(
        IEnumerable<BackupSchemaState> schemas,
        int formatVersion = BackupManifest.CurrentFormatVersion,
        string serverVersion = "16.4",
        IEnumerable<string>? unrecognized = null) =>
        new()
        {
            FormatVersion = formatVersion,
            CreatedAt = DateTimeOffset.UnixEpoch,
            DatabaseName = "cinomni",
            ServerVersion = serverVersion,
            DumpFileName = "cinomni-20260729T030000Z.dump",
            DumpSha256 = new string('0', 64),
            Schemas = [.. schemas],
            UnrecognizedMigrations = [.. unrecognized ?? []],
        };

    [Fact]
    public void A_dump_taken_by_this_exact_build_is_restorable_with_nothing_to_migrate()
    {
        var report = RestoreCompatibility.Check(
            Manifest([new BackupSchemaState("operations", OperationsMigrations)]),
            ThisBuild(("operations", OperationsMigrations)),
            "16.4");

        Assert.True(report.IsCompatible);
        Assert.Null(report.RefusalCode);
        Assert.Empty(report.Details);
    }

    /// <summary>The normal forward path: an older dump, restored into a newer build, migrates on startup.</summary>
    [Fact]
    public void An_older_dump_is_allowed_and_the_pending_migrations_are_named()
    {
        var report = RestoreCompatibility.Check(
            Manifest([new BackupSchemaState("operations", [OperationsMigrations[0]])]),
            ThisBuild(("operations", OperationsMigrations)),
            "16.4");

        Assert.True(report.IsCompatible);
        Assert.Contains(
            report.Details,
            detail => detail.Contains("20260723222008_CommandQueueAndScheduler", StringComparison.Ordinal));
    }

    /// <summary>A module added since the dump is not a mismatch: its schema is simply created on startup.</summary>
    [Fact]
    public void A_schema_this_build_added_since_the_dump_is_allowed_and_reported()
    {
        var report = RestoreCompatibility.Check(
            Manifest([new BackupSchemaState("operations", OperationsMigrations)]),
            ThisBuild(("operations", OperationsMigrations), ("requests", ["20260726120000_InitialRequests"])),
            "16.4");

        Assert.True(report.IsCompatible);
        Assert.Contains(report.Details, detail => detail.Contains("requests", StringComparison.Ordinal));
    }

    /// <summary>
    /// The refusal this whole feature exists for. A migration id the running build does not carry means
    /// the dump came from a newer Cinomni, and no amount of migrating gets the code back to it.
    /// </summary>
    [Fact]
    public void A_dump_from_a_newer_build_is_refused_naming_the_schema_and_the_migration()
    {
        var report = RestoreCompatibility.Check(
            Manifest([new BackupSchemaState("operations", [.. OperationsMigrations, "29990101000000_FromTheFuture"])]),
            ThisBuild(("operations", OperationsMigrations)),
            "16.4");

        Assert.False(report.IsCompatible);
        Assert.Equal("backup.dump_from_newer_build", report.RefusalCode);
        Assert.Contains(
            report.Details,
            detail => detail.Contains("operations", StringComparison.Ordinal)
                && detail.Contains("29990101000000_FromTheFuture", StringComparison.Ordinal));
    }

    /// <summary>
    /// The installation keeps one migration history for every module, so a dump taken by a build with a
    /// module this one has never had carries ids that belong to <b>no</b> schema in the manifest. The
    /// per-schema rule cannot see them; this is what does.
    /// </summary>
    [Fact]
    public void A_migration_no_module_in_this_build_owns_is_refused_naming_it()
    {
        var report = RestoreCompatibility.Check(
            Manifest(
                [new BackupSchemaState("operations", OperationsMigrations)],
                unrecognized: ["20270301090000_InitialAnalytics"]),
            ThisBuild(("operations", OperationsMigrations)),
            "16.4");

        Assert.False(report.IsCompatible);
        Assert.Equal("backup.dump_from_newer_build", report.RefusalCode);
        Assert.Contains(
            report.Details,
            detail => detail.Contains("20270301090000_InitialAnalytics", StringComparison.Ordinal));
    }

    /// <summary>A dump from the same build claims every applied migration, so the list is empty.</summary>
    [Fact]
    public void A_dump_with_no_unclaimed_migrations_passes_the_ownership_rule()
    {
        var report = RestoreCompatibility.Check(
            Manifest([new BackupSchemaState("operations", OperationsMigrations)], unrecognized: []),
            ThisBuild(("operations", OperationsMigrations)),
            "16.4");

        Assert.True(report.IsCompatible);
    }

    [Fact]
    public void A_dump_holding_a_schema_this_build_does_not_own_is_refused()
    {
        var report = RestoreCompatibility.Check(
            Manifest(
            [
                new BackupSchemaState("operations", OperationsMigrations),
                new BackupSchemaState("experiments", ["20270101000000_InitialExperiments"]),
            ]),
            ThisBuild(("operations", OperationsMigrations)),
            "16.4");

        Assert.False(report.IsCompatible);
        Assert.Equal("backup.unknown_schema", report.RefusalCode);
        Assert.Contains(report.Details, detail => detail.Contains("experiments", StringComparison.Ordinal));
    }

    [Fact]
    public void A_manifest_in_a_format_this_build_does_not_read_is_refused()
    {
        var report = RestoreCompatibility.Check(
            Manifest([new BackupSchemaState("operations", OperationsMigrations)], formatVersion: 99),
            ThisBuild(("operations", OperationsMigrations)),
            "16.4");

        Assert.False(report.IsCompatible);
        Assert.Equal("backup.unsupported_format", report.RefusalCode);
    }

    [Fact]
    public void A_dump_from_a_newer_postgresql_major_is_refused()
    {
        var report = RestoreCompatibility.Check(
            Manifest([new BackupSchemaState("operations", OperationsMigrations)], serverVersion: "17.2"),
            ThisBuild(("operations", OperationsMigrations)),
            "16.4");

        Assert.False(report.IsCompatible);
        Assert.Equal("backup.older_server", report.RefusalCode);
    }

    [Fact]
    public void A_dump_from_an_older_postgresql_major_is_allowed()
    {
        var report = RestoreCompatibility.Check(
            Manifest([new BackupSchemaState("operations", OperationsMigrations)], serverVersion: "15.6"),
            ThisBuild(("operations", OperationsMigrations)),
            "16.4");

        Assert.True(report.IsCompatible);
    }

    /// <summary>
    /// A check run before the database is up is still worth running. It answers what it can and skips the
    /// server rule rather than guessing a version and refusing — or allowing — on a fiction.
    /// </summary>
    [Fact]
    public void An_unreachable_target_server_skips_the_version_rule_instead_of_inventing_an_answer()
    {
        var report = RestoreCompatibility.Check(
            Manifest([new BackupSchemaState("operations", OperationsMigrations)], serverVersion: "17.2"),
            ThisBuild(("operations", OperationsMigrations)),
            targetServerVersion: null);

        Assert.True(report.IsCompatible);
    }

    /// <summary>
    /// The format check runs first on purpose: a manifest this build cannot read must not be interpreted
    /// far enough to produce a more specific — and wrong — refusal.
    /// </summary>
    [Fact]
    public void The_format_version_is_checked_before_anything_is_interpreted()
    {
        var report = RestoreCompatibility.Check(
            Manifest(
                [new BackupSchemaState("unknown-schema", ["29990101000000_FromTheFuture"])],
                formatVersion: 2,
                serverVersion: "99.0"),
            ThisBuild(("operations", OperationsMigrations)),
            "16.4");

        Assert.Equal("backup.unsupported_format", report.RefusalCode);
    }
}
