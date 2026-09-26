using Cinomni.Acquisition;
using Cinomni.Acquisition.Persistence;
using Cinomni.Catalog;
using Cinomni.Catalog.Persistence;
using Cinomni.Decision;
using Cinomni.Decision.Persistence;
using Cinomni.Discovery;
using Cinomni.Discovery.Persistence;
using Cinomni.Downloads;
using Cinomni.Downloads.Persistence;
using Cinomni.Identity;
using Cinomni.Identity.Contracts;
using Cinomni.Identity.Persistence;
using Cinomni.Import;
using Cinomni.Import.Persistence;
using Cinomni.Library;
using Cinomni.Library.Persistence;
using Cinomni.Metadata;
using Cinomni.Metadata.Persistence;
using Cinomni.Monitoring;
using Cinomni.Monitoring.Persistence;
using Cinomni.Notifications;
using Cinomni.Notifications.Persistence;
using Cinomni.Operations;
using Cinomni.Operations.Backup;
using Cinomni.Operations.Persistence;
using Cinomni.Playback;
using Cinomni.Playback.Persistence;
using Cinomni.ReleaseParsing;
using Cinomni.ReleaseParsing.Persistence;
using Cinomni.Requests;
using Cinomni.Requests.Persistence;
using Cinomni.Subtitles;
using Cinomni.Subtitles.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Cinomni.Host.Tests;

/// <summary>
/// The restore drill. Not "the scripts exist" — this takes a real backup of a real database with the real
/// <c>pg_dump</c>, destroys the database, restores it, and then asserts the one thing that decides whether
/// a backup was worth taking: <b>the current build boots against the restored database with nothing left
/// to do</b>.
/// <para>
/// The seeded rows are supporting evidence, chosen for what is irreplaceable rather than for coverage: an
/// account (with the credential verifier that cannot be recreated), a download checkpoint (a <c>bytea</c>
/// column, which is where a naive dump-and-restore goes wrong), and a queued command (work that was in
/// flight when the backup was taken and must still be in flight afterwards).
/// </para>
/// <para>
/// This is the only test in the repository that launches the PostgreSQL client tools, and they are a
/// real dependency: <c>postgresql-client</c> 16 or newer must be on <c>PATH</c>, or
/// <c>Backup__PgDumpPath</c> and <c>Backup__PgRestorePath</c> must name one — which is how CI pins the
/// major version instead of trusting a name to resolve to it. Where it is
/// <b>mandatory</b> — an explicit <c>CINOMNI_REQUIRE_BACKUP_DRILL=1</c>, which the pipeline that has both
/// sets — a missing tool fails this test by name, so the pipeline that is supposed to prove the drill can
/// never quietly stop running it. On a contributor machine without the client it is reported as skipped
/// with the reason, because a developer must not be blocked from running the suite by an operational
/// dependency they have no reason to install — and because a test that returns without asserting would be
/// reported as passed, which is the one outcome a drill must never produce.
/// </para>
/// </summary>
public sealed class BackupRestoreDrillTests : IAsyncLifetime
{
    private const string Database = "cinomni_test_backup_drill";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"cinomni-drill-{Guid.NewGuid():N}");

    private ServiceProvider _provider = null!;

    private static string ConnectionStringFor(string database) =>
        $"Host=localhost;Port=5442;Database={database};Username=cinomni;Password=cinomni_dev";

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);

        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.None));

        // The real composition root, so the drill exercises the installation rather than a copy of it.
        services.AddCinomniModules(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Backup:Root"] = _root,
                    // No schedule: the drill drives the run itself.
                    ["Backup:Enabled"] = "false",
                    ["Backup:PgDumpPath"] =
                        BackupDrillPrerequisites.ToolPath("Backup__PgDumpPath", "pg_dump"),
                    ["Backup:PgRestorePath"] =
                        BackupDrillPrerequisites.ToolPath("Backup__PgRestorePath", "pg_restore"),
                    // Storage roots inside the drill's own directory, so nothing points at a real tree.
                    ["Import:LibraryRoot"] = Path.Combine(_root, "library"),
                    ["Downloads:Sidecar:StagingPath"] = Path.Combine(_root, "downloads"),
                    ["Playback:TranscodeRoot"] = Path.Combine(_root, "transcodes"),
                })
                .Build(),
            ConnectionStringFor(Database));

        _provider = services.BuildServiceProvider();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory must never fail a test run.
        }
    }

    [BackupDrillFact]
    public async Task A_restored_database_holds_what_was_irreplaceable_and_boots_this_build_with_nothing_to_do()
    {
        // Reaching here with a prerequisite missing means this run is one that must prove the drill: the
        // attribute only skips where it is optional. A pipeline that has lost its client tools or its
        // server has to hear which, by name, rather than watch the drill quietly stop running.
        if (BackupDrillPrerequisites.UnmetReason() is { } missing)
        {
            Assert.Fail(missing);
        }

        // 1. A database as an installation has it: every schema migrated, recovery run.
        await RecreateDatabaseAsync();
        await MigrateEverythingAsync();
        await _provider.RecoverOperationsAsync();

        // 2. One row per irreplaceable concern.
        var seeded = await SeedAsync();

        // 3. A real backup, through the real pg_dump.
        BackupManifest manifest;
        await using (var scope = _provider.CreateAsyncScope())
        {
            var outcome = await scope.ServiceProvider.GetRequiredService<BackupService>()
                .CreateAsync(BackupTrigger.Manual);
            Assert.True(outcome.IsSuccess, outcome.IsFailure ? outcome.Error.Message : null);
            manifest = outcome.Value;
        }

        Assert.Equal(16, manifest.Schemas.Count);
        Assert.All(manifest.Schemas, schema => Assert.NotEmpty(schema.AppliedMigrations));

        // Every applied migration belongs to exactly one module. The installation keeps a single
        // migrations-history table, so reading a context's applied list naively gives every context the
        // whole installation's list — a manifest that then refuses its own restore. This is the guard.
        var claimed = manifest.Schemas.SelectMany(schema => schema.AppliedMigrations).ToArray();
        Assert.Equal(claimed.Length, claimed.Distinct(StringComparer.Ordinal).Count());
        Assert.Empty(manifest.UnrecognizedMigrations);

        var set = Assert.Single(_provider.GetRequiredService<BackupStore>().List());

        // 4. Lose the database completely. This is the drill, not a metaphor for one.
        await RecreateDatabaseAsync();
        await AssertDatabaseIsEmptyAsync();

        // 5. Restore, through the same hardened launcher the operator script invokes by hand.
        var restore = await _provider.GetRequiredService<IDatabaseDumpRunner>()
            .RestoreAsync(set.DumpPath, Database);
        Assert.True(restore.IsSuccess, restore.IsFailure ? restore.Error.Message : null);

        // 6a. The irreplaceable rows are back, byte for byte.
        await AssertSeedSurvivedAsync(seeded);

        // 6b. The compatibility gate agrees this dump belongs to this build.
        await using (var scope = _provider.CreateAsyncScope())
        {
            var report = RestoreCompatibility.Check(
                manifest,
                scope.ServiceProvider.GetRequiredService<SchemaInventory>().ReadKnown(),
                await ReadServerVersionAsync());

            Assert.True(
                report.IsCompatible,
                $"{report.Summary} {string.Join(" | ", report.Details)}");
            Assert.Empty(report.Details);
        }

        // 6c. The point of the drill: the current build has nothing left to migrate, and the whole startup
        // sequence — every migration then recovery — runs again cleanly against the restored database.
        await AssertNothingPendingAsync();
        await MigrateEverythingAsync();
        await _provider.RecoverOperationsAsync();
        await AssertNothingPendingAsync();
        await AssertSeedSurvivedAsync(seeded);

        // 7. The other half of the contract: a restore needs an empty database. The target now holds an
        // installation, so this must fail outright rather than merge two of them on top of each other.
        var overExisting = await _provider.GetRequiredService<IDatabaseDumpRunner>()
            .RestoreAsync(set.DumpPath, Database);
        Assert.True(overExisting.IsFailure, "A restore into a populated database must not succeed.");
        await AssertSeedSurvivedAsync(seeded);
    }

    private static async Task RecreateDatabaseAsync()
    {
        await using var connection = new NpgsqlConnection(
            BackupDrillPrerequisites.ServerConnectionString + ";Database=postgres");
        await connection.OpenAsync();

        // Identifiers are compile-time constants in this file; nothing here comes from outside it.
        await using (var drop = connection.CreateCommand())
        {
            drop.CommandText = $"DROP DATABASE IF EXISTS {Database} WITH (FORCE)";
            await drop.ExecuteNonQueryAsync();
        }

        await using (var create = connection.CreateCommand())
        {
            create.CommandText = $"CREATE DATABASE {Database}";
            await create.ExecuteNonQueryAsync();
        }

        // The pool still holds sessions against the database that was just dropped.
        NpgsqlConnection.ClearAllPools();
    }

    private static async Task<string> ReadServerVersionAsync()
    {
        await using var connection = new NpgsqlConnection(ConnectionStringFor(Database));
        await connection.OpenAsync();
        return connection.PostgreSqlVersion.ToString();
    }

    /// <summary>The Host's startup sequence, in the order <c>Program</c> runs it.</summary>
    private async Task MigrateEverythingAsync()
    {
        await _provider.MigrateOperationsAsync();
        await _provider.MigrateIdentityAsync();
        await _provider.MigrateCatalogAsync();
        await _provider.MigrateMetadataAsync();
        await _provider.MigrateMonitoringAsync();
        await _provider.MigrateDiscoveryAsync();
        await _provider.MigrateReleaseParsingAsync();
        await _provider.MigrateDecisionAsync();
        await _provider.MigrateAcquisitionAsync();
        await _provider.MigrateDownloadsAsync();
        await _provider.MigrateImportAsync();
        await _provider.MigrateLibraryAsync();
        await _provider.MigratePlaybackAsync();
        await _provider.MigrateSubtitlesAsync();
        await _provider.MigrateRequestsAsync();
        await _provider.MigrateNotificationsAsync();
    }

    /// <summary>
    /// Every context reports zero pending migrations. This is what "the restored database boots the
    /// current build" means in a form a test can assert.
    /// </summary>
    private async Task AssertNothingPendingAsync()
    {
        await using var scope = _provider.CreateAsyncScope();

        foreach (var context in EveryContext(scope.ServiceProvider))
        {
            var pending = await context.Database.GetPendingMigrationsAsync();
            Assert.True(
                !pending.Any(),
                $"{context.GetType().Name} has pending migrations after the restore: {string.Join(", ", pending)}");
        }
    }

    private async Task AssertDatabaseIsEmptyAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();

        var applied = await context.Database.GetAppliedMigrationsAsync();
        Assert.Empty(applied);
    }

    private static IEnumerable<DbContext> EveryContext(IServiceProvider scope) =>
    [
        scope.GetRequiredService<OperationsDbContext>(),
        scope.GetRequiredService<IdentityDbContext>(),
        scope.GetRequiredService<CatalogDbContext>(),
        scope.GetRequiredService<MetadataDbContext>(),
        scope.GetRequiredService<MonitoringDbContext>(),
        scope.GetRequiredService<DiscoveryDbContext>(),
        scope.GetRequiredService<ReleaseParsingDbContext>(),
        scope.GetRequiredService<DecisionDbContext>(),
        scope.GetRequiredService<AcquisitionDbContext>(),
        scope.GetRequiredService<DownloadsDbContext>(),
        scope.GetRequiredService<ImportDbContext>(),
        scope.GetRequiredService<LibraryDbContext>(),
        scope.GetRequiredService<PlaybackDbContext>(),
        scope.GetRequiredService<SubtitlesDbContext>(),
        scope.GetRequiredService<RequestsDbContext>(),
        scope.GetRequiredService<NotificationsDbContext>(),
    ];

    /// <summary>What was seeded, so the assertions compare against exact values rather than shapes.</summary>
    private sealed record Seed(Guid UserId, string PasswordHash, Guid DownloadTaskId, byte[] ResumeData, string CommandKey);

    private async Task<Seed> SeedAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var resumeData = new byte[512];
        Random.Shared.NextBytes(resumeData);
        // A NUL and a high byte in fixed positions: the two things a text round trip would destroy.
        resumeData[0] = 0x00;
        resumeData[1] = 0xFF;

        const string PasswordHash = "$argon2id$v=19$m=65536,t=3,p=4$ZHJpbGxzYWx0$ZHJpbGx2ZXJpZmllcg";
        var commandKey = $"drill:{Guid.NewGuid():N}";

        await using var scope = _provider.CreateAsyncScope();

        var identity = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "drill-operator",
            PasswordHash = PasswordHash,
            Role = UserRole.Administrator,
            CreatedAt = now,
        };
        identity.Users.Add(user);
        await identity.SaveChangesAsync();

        var downloads = scope.ServiceProvider.GetRequiredService<DownloadsDbContext>();
        var task = DownloadTask.Create(
            intentId: Guid.NewGuid(),
            attemptId: Guid.NewGuid(),
            workId: Guid.NewGuid(),
            targetId: Guid.NewGuid(),
            releaseGuid: "drill-release",
            downloadUrl: "magnet:?xt=urn:btih:0000000000000000000000000000000000000000",
            savePath: Path.Combine(_root, "downloads"),
            policy: SeedingPolicy.Unbounded,
            now: now);
        task.SaveCheckpoint(resumeData, now);
        downloads.Tasks.Add(task);
        await downloads.SaveChangesAsync();

        var operations = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
        operations.Commands.Add(new QueuedCommand
        {
            Id = Guid.NewGuid(),
            CommandType = "drill.in-flight",
            Payload = """{"note":"work that was queued when the backup was taken"}""",
            IdempotencyKey = commandKey,
            State = CommandState.Queued,
            MaxAttempts = 5,
            QueuedAt = now,
        });
        await operations.SaveChangesAsync();

        return new Seed(user.Id, PasswordHash, task.Id, resumeData, commandKey);
    }

    private async Task AssertSeedSurvivedAsync(Seed seed)
    {
        await using var scope = _provider.CreateAsyncScope();

        var user = await scope.ServiceProvider.GetRequiredService<IdentityDbContext>()
            .Users.AsNoTracking().SingleAsync(entity => entity.Id == seed.UserId);
        Assert.Equal("drill-operator", user.Username);
        // The credential verifier cannot be recreated from anything: losing it locks the household out.
        Assert.Equal(seed.PasswordHash, user.PasswordHash);
        Assert.Equal(UserRole.Administrator, user.Role);

        var task = await scope.ServiceProvider.GetRequiredService<DownloadsDbContext>()
            .Tasks.AsNoTracking().SingleAsync(entity => entity.Id == seed.DownloadTaskId);
        // The resume-data checkpoint is what lets a download continue instead of starting over.
        Assert.NotNull(task.ResumeData);
        Assert.Equal(seed.ResumeData, task.ResumeData);

        var command = await scope.ServiceProvider.GetRequiredService<OperationsDbContext>()
            .Commands.AsNoTracking().SingleAsync(entity => entity.IdempotencyKey == seed.CommandKey);
        Assert.Equal(CommandState.Queued, command.State);
    }
}
