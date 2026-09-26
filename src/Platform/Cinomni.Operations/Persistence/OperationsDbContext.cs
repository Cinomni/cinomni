using Cinomni.Operations.Diagnostics;
using Microsoft.EntityFrameworkCore;

namespace Cinomni.Operations.Persistence;

/// <summary>
/// EF Core context for the platform kernel (Operations). Owns the <c>operations</c>
/// schema; no other module reads or writes its tables.
/// </summary>
public sealed class OperationsDbContext(DbContextOptions<OperationsDbContext> options)
    : DbContext(options)
{
    public const string SchemaName = "operations";

    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();

    public DbSet<QueuedCommand> Commands => Set<QueuedCommand>();

    public DbSet<ScheduledJob> ScheduledJobs => Set<ScheduledJob>();

    public DbSet<BackupRun> BackupRuns => Set<BackupRun>();

    public DbSet<Setting> Settings => Set<Setting>();

    public DbSet<SettingAudit> SettingAudits => Set<SettingAudit>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(SchemaName);

        var outbox = modelBuilder.Entity<OutboxMessage>();
        outbox.ToTable("outbox");
        outbox.HasKey(x => x.Id);
        outbox.Property(x => x.EventType).HasMaxLength(200);
        outbox.Property(x => x.IdempotencyKey).HasMaxLength(200);
        outbox.Property(x => x.Payload).HasColumnType("jsonb");
        // Trace context is fixed-shape metadata, not payload: nullable and bounded, added expand-only so
        // every row written before it existed still relays.
        outbox.Property(x => x.TraceParent).HasMaxLength(MessageTracing.TraceParentLength);
        outbox.Property(x => x.TraceState).HasMaxLength(MessageTracing.TraceStateLength);
        outbox.Property(x => x.LastError).HasMaxLength(200);
        outbox.Property(x => x.Attempts).HasDefaultValue(0);
        // What the relay can still deliver: unpublished and not given up on.
        outbox.HasIndex(x => x.OccurredAt)
            .HasDatabaseName("ix_outbox_unpublished")
            .HasFilter("published = false AND dead_lettered_at IS NULL");
        // The relay index above is filtered on the inverse predicate, so it cannot serve the purge.
        // This one covers "published and old enough", which is the whole retention rule.
        outbox.HasIndex(x => x.PublishedAt)
            .HasDatabaseName("ix_outbox_purge")
            .HasFilter("published = true");

        var command = modelBuilder.Entity<QueuedCommand>();
        command.ToTable("command", t =>
            t.HasCheckConstraint("ck_command_state", "state IN ('Queued','Running','Completed','Failed')"));
        command.HasKey(x => x.Id);
        command.Property(x => x.CommandType).HasMaxLength(200);
        command.Property(x => x.Payload).HasColumnType("jsonb");
        command.Property(x => x.IdempotencyKey).HasMaxLength(200);
        command.Property(x => x.State).HasMaxLength(20);
        command.Property(x => x.TraceParent).HasMaxLength(MessageTracing.TraceParentLength);
        command.Property(x => x.TraceState).HasMaxLength(MessageTracing.TraceStateLength);
        command.HasIndex(x => x.IdempotencyKey).IsUnique().HasDatabaseName("ux_command_idempotency_key");
        // Partial index over the claimable rows drives the worker query cheaply.
        command.HasIndex(x => x.QueuedAt)
            .HasDatabaseName("ix_command_queued")
            .HasFilter("state = 'Queued'");
        // The retention purge walks the terminal states instead, which the filtered index above
        // excludes by definition.
        command.HasIndex(x => new { x.State, x.QueuedAt })
            .HasDatabaseName("ix_command_purge");

        var backupRun = modelBuilder.Entity<BackupRun>();
        backupRun.ToTable("backup_run", t =>
        {
            t.HasCheckConstraint(
                "ck_backup_run_outcome",
                "outcome IN ('Running','Succeeded','Failed','Skipped','Interrupted')");
            t.HasCheckConstraint("ck_backup_run_trigger", "triggered_by IN ('Scheduled','Manual')");
        });
        backupRun.HasKey(x => x.Id);
        backupRun.Property(x => x.TriggeredBy).HasMaxLength(20);
        backupRun.Property(x => x.Outcome).HasMaxLength(20);
        backupRun.Property(x => x.Stamp).HasMaxLength(40);
        backupRun.Property(x => x.DumpSha256).HasMaxLength(64);
        backupRun.Property(x => x.Reason).HasMaxLength(500);
        // The only access path is "the most recent runs, newest first" — the backup worker reads it on
        // every tick to decide whether one is due.
        backupRun.HasIndex(x => x.StartedAt)
            .IsDescending()
            .HasDatabaseName("ix_backup_run_started");

        var job = modelBuilder.Entity<ScheduledJob>();
        job.ToTable("scheduled_job");
        job.HasKey(x => x.Name);
        job.Property(x => x.Name).HasMaxLength(200);
        job.Property(x => x.CommandType).HasMaxLength(200);
        job.HasIndex(x => x.NextDue)
            .HasDatabaseName("ix_scheduled_job_due")
            .HasFilter("enabled = true");

        var setting = modelBuilder.Entity<Setting>();
        setting.ToTable("setting", t => t.HasCheckConstraint(
            "ck_setting_value_xor_secret", "(value IS NULL) <> (secret_cipher IS NULL)"));
        setting.HasKey(x => x.Key);
        setting.Property(x => x.Key).HasMaxLength(200);
        setting.Property(x => x.Value).HasColumnType("text");
        setting.Property(x => x.SecretCipher).HasColumnType("bytea");
        setting.Property(x => x.SecretNonce).HasColumnType("bytea");
        setting.Property(x => x.KeyId).HasMaxLength(32);
        setting.Property(x => x.Fingerprint).HasMaxLength(8);
        // The write path's per-key optimistic concurrency (writePath step 5): EF includes the row's
        // originally-loaded Version in every UPDATE/DELETE's WHERE clause, so a write that targets a row
        // another writer already moved affects zero rows and surfaces as DbUpdateConcurrencyException
        // instead of silently overwriting a change the caller never saw.
        setting.Property(x => x.Version).HasDefaultValue(1L).IsConcurrencyToken();
        // No secondary index: the table is tens of rows and is always read whole into the in-process
        // cache (readPath), never queried by a predicate.

        var settingAudit = modelBuilder.Entity<SettingAudit>();
        settingAudit.ToTable("setting_audit", t => t.HasCheckConstraint(
            "ck_setting_audit_action", "action IN ('Set','Cleared')"));
        settingAudit.HasKey(x => x.Id);
        settingAudit.Property(x => x.Key).HasMaxLength(200);
        settingAudit.Property(x => x.Action).HasMaxLength(20);
        // Bounded generously: a non-secret display value can be a full list/duration/number rendering,
        // not just a short scalar. A secret row's display is always the 8-hex fingerprint, well under it.
        settingAudit.Property(x => x.OldDisplay).HasMaxLength(500);
        settingAudit.Property(x => x.NewDisplay).HasMaxLength(500);
        // The only access path: "history of this key, newest first" (for a per-field audit trail).
        settingAudit.HasIndex(x => new { x.Key, x.ChangedAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_setting_audit_key_changed_at");
    }
}
