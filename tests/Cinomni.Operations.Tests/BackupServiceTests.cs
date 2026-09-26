using Cinomni.Kernel.Results;
using Cinomni.Operations.Backup;
using Cinomni.Operations.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Cinomni.Operations.Tests;

/// <summary>
/// The backup run itself, against a real PostgreSQL instance, with the external tool faked so the suite
/// asserts the ordering and the recovery behaviour rather than <c>pg_dump</c>'s.
/// <para>
/// The end-to-end proof — a real dump, a real restore and a database the current build boots against —
/// is the drill in the Host test project. This one covers what the drill cannot: an interrupted run, a
/// concurrent run, what each of those leaves in <c>operations.backup_run</c>, and the invariant that a
/// dump never occupies the shared command worker.
/// </para>
/// </summary>
public sealed class BackupServiceTests : IAsyncLifetime
{
    private const string Database = "cinomni_test_backup";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"cinomni-backup-service-{Guid.NewGuid():N}");
    private readonly FakeDumpRunner _runner = new();
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);

        // BackupService now reads BackupOptions.KeepCount through ILiveOptions<BackupOptions>, whose
        // binder resolves it from the settings store, then IConfiguration, then the options instance
        // below only as a last resort — exactly the chain BackupComposition.AddConfiguredBackup uses in
        // the real Host. A raw C# callback alone is invisible to that chain, so the same KeepCount is
        // also supplied as configuration here.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Backup:KeepCount"] = "2" })
            .Build();

        _provider = await OperationsTestHost.CreateAsync(
            Database,
            services =>
            {
                services.AddBackup(
                    OperationsTestHost.ConnectionStringFor(Database),
                    options =>
                    {
                        options.Root = _root;
                        options.KeepCount = 2;
                        options.Interval = TimeSpan.FromDays(1);
                    });

                services.AddBackupSchema<OperationsDbContext>(OperationsDbContext.SchemaName);

                // Last registration wins: the fake replaces the pg_dump adapter for the whole provider.
                services.AddSingleton<IDatabaseDumpRunner>(_runner);
            },
            configuration: configuration);
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

    private async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> work)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await work(scope.ServiceProvider);
    }

    /// <summary>One run, through the same entry point the worker and the CLI both use.</summary>
    private Task<Result<BackupManifest>> RunAsync(string triggeredBy = BackupTrigger.Scheduled) =>
        InScopeAsync(scope => scope.GetRequiredService<BackupService>().CreateAsync(triggeredBy));

    private async Task<List<BackupRun>> ReadRunsAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OperationsDbContext>()
            .BackupRuns.AsNoTracking().OrderBy(run => run.StartedAt).ToListAsync();
    }

    [Fact]
    public async Task A_run_writes_a_dump_and_a_manifest_that_describes_it()
    {
        var outcome = await RunAsync();

        Assert.True(outcome.IsSuccess, outcome.IsFailure ? outcome.Error.Message : null);

        var manifest = outcome.Value;
        Assert.Equal(BackupManifest.CurrentFormatVersion, manifest.FormatVersion);
        Assert.Equal(Database, manifest.DatabaseName);
        Assert.NotEmpty(manifest.ServerVersion);

        // The manifest records the schema state the restore gate is checked against.
        var operations = Assert.Single(manifest.Schemas);
        Assert.Equal(OperationsDbContext.SchemaName, operations.Name);
        Assert.NotEmpty(operations.AppliedMigrations);

        var store = _provider.GetRequiredService<BackupStore>();
        var set = Assert.Single(store.List());
        Assert.Equal(manifest.DumpFileName, Path.GetFileName(set.DumpPath));
        Assert.Equal(await BackupStore.ComputeSha256Async(set.DumpPath), manifest.DumpSha256);

        var verified = await InScopeAsync(scope =>
            scope.GetRequiredService<BackupService>().VerifyAsync(set));
        Assert.True(verified.IsSuccess);
    }

    /// <summary>
    /// "When did this installation last back itself up?" has to be answerable from the database:
    /// backup has no HTTP surface, so a directory listing is otherwise the only answer.
    /// </summary>
    [Fact]
    public async Task A_successful_run_is_recorded_with_what_it_produced()
    {
        var outcome = await RunAsync(BackupTrigger.Manual);
        Assert.True(outcome.IsSuccess, outcome.IsFailure ? outcome.Error.Message : null);

        var run = Assert.Single(await ReadRunsAsync());
        Assert.Equal(BackupRunOutcome.Succeeded, run.Outcome);
        Assert.Equal(BackupTrigger.Manual, run.TriggeredBy);
        Assert.NotNull(run.CompletedAt);
        Assert.Equal(outcome.Value.DumpSha256, run.DumpSha256);
        Assert.Equal(outcome.Value.DumpSizeBytes, run.DumpSizeBytes);
        Assert.Equal(outcome.Value.DumpFileName, $"cinomni-{run.Stamp}.dump");
        Assert.Null(run.Reason);
    }

    /// <summary>
    /// A dump copied off the machine can be truncated in transit, and a backup nobody checked is a
    /// backup nobody has. Verification is what turns that into a refusal before the restore.
    /// </summary>
    [Fact]
    public async Task A_dump_that_no_longer_matches_its_manifest_is_refused()
    {
        await RunAsync();

        var set = Assert.Single(_provider.GetRequiredService<BackupStore>().List());
        await File.AppendAllTextAsync(set.DumpPath, "corruption");

        var verified = await InScopeAsync(scope =>
            scope.GetRequiredService<BackupService>().VerifyAsync(set));

        Assert.True(verified.IsFailure);
        Assert.Equal("backup.corrupt", verified.Error.Code);
    }

    /// <summary>
    /// The interruption case: the tool wrote part of a dump and then died. What must survive is that no
    /// manifest exists, so the half-written file is not a backup and never will be — and the previous
    /// backups are untouched, because retention only runs after a complete set exists.
    /// </summary>
    [Fact]
    public async Task An_interrupted_run_leaves_no_backup_and_costs_no_existing_one()
    {
        await RunAsync();
        var before = _provider.GetRequiredService<BackupStore>().List();

        // A stamp has one-second resolution, so the failing run must land in a later second to be a
        // second run rather than a refusal to overwrite the first.
        await Task.Delay(TimeSpan.FromSeconds(1.1));
        _runner.FailAfterWriting = true;
        var outcome = await RunAsync();

        Assert.True(outcome.IsFailure);
        Assert.Equal("backup.dump_failed", outcome.Error.Code);

        Assert.Equal(before.Select(set => set.Stamp), _provider.GetRequiredService<BackupStore>().List().Select(set => set.Stamp));
        Assert.NotEmpty(Directory.GetFiles(_root, "*.dump.partial"));
        Assert.Single(Directory.GetFiles(_root, "*.manifest.json"));

        // An installation with no working backup is discoverable, and it carries the reason.
        var failed = (await ReadRunsAsync())[^1];
        Assert.Equal(BackupRunOutcome.Failed, failed.Outcome);
        Assert.NotNull(failed.Reason);
        Assert.Contains("backup.dump_failed", failed.Reason, StringComparison.Ordinal);
        Assert.Null(failed.Stamp);
    }

    /// <summary>
    /// An installation that cannot back itself up must not fill its own disk trying. Retention proper
    /// runs only after a success, so a run that never succeeds would accumulate one partial per attempt
    /// for ever — and the space the next success needs is exactly what those partials hold. Sweeping at
    /// the start of a run breaks that loop without touching anything a live run could be writing.
    /// </summary>
    [Fact]
    public async Task A_run_that_keeps_failing_still_reclaims_the_partials_of_earlier_attempts()
    {
        _runner.FailAfterWriting = true;

        Assert.True((await RunAsync()).IsFailure);
        var abandoned = Assert.Single(Directory.GetFiles(_root, "*.dump.partial"));

        // Old enough that no run could still be writing to it — the sweep's own definition.
        File.SetLastWriteTimeUtc(abandoned, DateTime.UtcNow.AddDays(-2));

        // A stamp has one-second resolution, so the next attempt must land in a later second.
        await Task.Delay(TimeSpan.FromSeconds(1.1));
        Assert.True((await RunAsync()).IsFailure);

        var remaining = Directory.GetFiles(_root, "*.dump.partial");
        Assert.Single(remaining);
        Assert.DoesNotContain(abandoned, remaining);
    }

    /// <summary>
    /// A run killed mid-dump leaves its row open. Nothing can be running while the advisory lock is
    /// free, so the next run that takes the lock is exactly the right place to close it — and it must,
    /// or "is a backup under way?" answers yes for ever.
    /// </summary>
    [Fact]
    public async Task A_run_that_never_finished_is_closed_by_the_next_one()
    {
        await using (var scope = _provider.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
            dbContext.BackupRuns.Add(new BackupRun
            {
                Id = Guid.CreateVersion7(),
                TriggeredBy = BackupTrigger.Scheduled,
                Outcome = BackupRunOutcome.Running,
                StartedAt = DateTimeOffset.UtcNow.AddHours(-3),
            });
            await dbContext.SaveChangesAsync();
        }

        Assert.True((await RunAsync()).IsSuccess);

        var runs = await ReadRunsAsync();
        Assert.Equal(2, runs.Count);
        Assert.Equal(BackupRunOutcome.Interrupted, runs[0].Outcome);
        Assert.NotNull(runs[0].CompletedAt);
        Assert.Equal(BackupRunOutcome.Succeeded, runs[1].Outcome);
    }

    /// <summary>
    /// Two runs cannot overlap. Losing the race is reported as "in progress" and never as an error: the
    /// other run is taking the backup, and turning this into a retry would queue work that exists only
    /// to lose the race again.
    /// </summary>
    [Fact]
    public async Task A_second_run_while_one_is_under_way_stops_cleanly_without_writing_anything()
    {
        // Hold the advisory lock on a session of its own, exactly as a run in flight would.
        await using var holder = new NpgsqlConnection(OperationsTestHost.ConnectionStringFor(Database));
        await holder.OpenAsync();
        await using (var command = holder.CreateCommand())
        {
            command.CommandText = $"SELECT pg_try_advisory_lock({BackupService.AdvisoryLockKey})";
            Assert.True((bool)(await command.ExecuteScalarAsync())!);
        }

        var outcome = await RunAsync();

        Assert.True(outcome.IsFailure);
        Assert.Equal(BackupService.InProgressCode, outcome.Error.Code);
        Assert.Empty(Directory.GetFiles(_root));

        // Losing the race is recorded too. A tick that produced no backup must never be indistinguishable
        // from one that did — that is the whole reason this table exists.
        var run = Assert.Single(await ReadRunsAsync());
        Assert.Equal(BackupRunOutcome.Skipped, run.Outcome);
        Assert.NotNull(run.Reason);
        Assert.Null(run.Stamp);
    }

    [Fact]
    public async Task Retention_keeps_the_configured_number_of_backups_across_runs()
    {
        for (var run = 0; run < 4; run++)
        {
            var outcome = await RunAsync();
            Assert.True(outcome.IsSuccess, outcome.IsFailure ? outcome.Error.Message : null);

            // The stamp has one-second resolution, which is also what makes two backups in the same
            // second refuse rather than overwrite.
            await Task.Delay(TimeSpan.FromSeconds(1.1));
        }

        Assert.Equal(2, _provider.GetRequiredService<BackupStore>().List().Count);
    }

    /// <summary>
    /// The invariant the backup worker exists for: a dump never occupies the single command worker.
    /// There is one, it dispatches its batch strictly sequentially, and a dump can run for hours — long
    /// enough for the thirty-second download checkpoint, whose resume data is the one thing here that
    /// cannot be re-fetched, to stop being written. So a run must enqueue nothing and no schedule row
    /// may claim it does.
    /// </summary>
    [Fact]
    public async Task A_run_puts_no_work_on_the_shared_command_queue()
    {
        await _provider.RecoverOperationsAsync();

        Assert.True((await RunAsync()).IsSuccess);
        Assert.Single(_provider.GetRequiredService<BackupStore>().List());
        Assert.Equal(1, _runner.DumpCount);

        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();

        Assert.Empty(await dbContext.Commands.AsNoTracking()
            .Where(command => command.CommandType.Contains("backup")).ToListAsync());
        Assert.Empty(await dbContext.ScheduledJobs.AsNoTracking()
            .Where(job => job.Name.Contains("backup")).ToListAsync());
    }

    /// <summary>
    /// A schedule row whose registration is gone must stop asserting that work is happening. This is the
    /// upgrade path for an installation that ran a build where backup was a scheduled job.
    /// </summary>
    [Fact]
    public async Task Startup_recovery_disables_a_scheduled_job_this_build_no_longer_registers()
    {
        await using (var scope = _provider.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
            dbContext.ScheduledJobs.Add(new ScheduledJob
            {
                Name = "operations.backup",
                CommandType = "operations.run-backup",
                IntervalSeconds = 86_400,
                NextDue = DateTimeOffset.UtcNow,
                Enabled = true,
            });
            await dbContext.SaveChangesAsync();
        }

        await _provider.RecoverOperationsAsync();

        await using (var scope = _provider.CreateAsyncScope())
        {
            var job = await scope.ServiceProvider.GetRequiredService<OperationsDbContext>()
                .ScheduledJobs.AsNoTracking().SingleAsync(entity => entity.Name == "operations.backup");
            Assert.False(job.Enabled);
        }

        // The retention job the platform does register is untouched by that sweep.
        await using (var scope = _provider.CreateAsyncScope())
        {
            var retention = await scope.ServiceProvider.GetRequiredService<OperationsDbContext>()
                .ScheduledJobs.AsNoTracking().SingleAsync(entity => entity.Name == "operations.retention");
            Assert.True(retention.Enabled);
        }
    }

    /// <summary>
    /// A missing <c>pg_dump</c> is an operational state, not a crash: it is returned as a failure and
    /// recorded, so an installation with no working backup is discoverable rather than silent.
    /// </summary>
    [Fact]
    public async Task A_missing_tool_is_reported_as_a_failure_the_journal_keeps()
    {
        _runner.ToolMissing = true;

        var outcome = await RunAsync();

        Assert.True(outcome.IsFailure);
        Assert.Equal("backup.tool_missing", outcome.Error.Code);
        Assert.Empty(Directory.GetFiles(_root, "*.dump"));

        var run = Assert.Single(await ReadRunsAsync());
        Assert.Equal(BackupRunOutcome.Failed, run.Outcome);
        Assert.Contains("backup.tool_missing", run.Reason!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The cadence lives in the journal, which is what lets it survive a restart: an installation that
    /// reboots hourly must not dump hourly, and one that was down when a backup was due takes it on the
    /// way back up.
    /// </summary>
    [Fact]
    public async Task The_next_run_is_due_an_interval_after_the_last_one_started()
    {
        var interval = TimeSpan.FromDays(1);

        await using var scope = _provider.CreateAsyncScope();
        var journal = scope.ServiceProvider.GetRequiredService<BackupJournal>();

        // Nothing recorded yet: a fresh installation backs itself up rather than waiting a day.
        Assert.Null(await journal.ReadLastRunAsync());

        Assert.True((await RunAsync()).IsSuccess);

        var last = await journal.ReadLastRunAsync();
        Assert.NotNull(last);
        Assert.Equal(BackupRunOutcome.Succeeded, last.Outcome);
        Assert.Equal(
            last.StartedAt + interval,
            BackupSchedule.NextDueAt(last, interval, DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// Stands in for <c>pg_dump</c>: writes recognisable bytes, and can be told to behave like a tool
    /// that is absent or that dies part-way through writing.
    /// </summary>
    private sealed class FakeDumpRunner : IDatabaseDumpRunner
    {
        public bool FailAfterWriting { get; set; }

        public bool ToolMissing { get; set; }

        public int DumpCount { get; private set; }

        public async Task<Result> DumpAsync(string targetFile, CancellationToken cancellationToken = default)
        {
            if (ToolMissing)
            {
                return Result.Failure(new Error("backup.tool_missing", "pg_dump is not installed."));
            }

            DumpCount++;
            await File.WriteAllTextAsync(targetFile, $"fake dump {DumpCount} {Guid.NewGuid():N}", cancellationToken);

            return FailAfterWriting
                ? Result.Failure(new Error("backup.dump_failed", "'pg_dump' exited with 1."))
                : Result.Success();
        }

        public Task<Result> RestoreAsync(
            string sourceFile,
            string databaseName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());

        public Task<Result<string>> ProbeVersionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(ToolMissing
                ? Result<string>.Failure(new Error("backup.tool_missing", "pg_dump is not installed."))
                : Result<string>.Success("pg_dump (PostgreSQL) 16.0 (fake)"));
    }
}
