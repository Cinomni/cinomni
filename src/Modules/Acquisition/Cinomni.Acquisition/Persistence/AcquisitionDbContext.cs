using Microsoft.EntityFrameworkCore;

namespace Cinomni.Acquisition.Persistence;

/// <summary>
/// EF Core context for the Acquisition module. Owns the <c>acquisition</c> schema:
/// the persistent goals plus their append-only attempt and history trails. CandidateSelection,
/// DownloadAssignment and ImportAssignment from the design fold into <see cref="AcquisitionAttempt"/>
/// for the movie slice — one row per candidate tried carries the selection and the hand-off.
/// </summary>
public sealed class AcquisitionDbContext(DbContextOptions<AcquisitionDbContext> options)
    : DbContext(options)
{
    public const string SchemaName = "acquisition";

    public DbSet<AcquisitionIntent> Intents => Set<AcquisitionIntent>();

    public DbSet<AcquisitionAttempt> Attempts => Set<AcquisitionAttempt>();

    public DbSet<AcquisitionAttemptUnit> AttemptUnits => Set<AcquisitionAttemptUnit>();

    public DbSet<AcquisitionHistoryRecord> History => Set<AcquisitionHistoryRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(SchemaName);

        var intent = modelBuilder.Entity<AcquisitionIntent>();
        intent.ToTable("acquisition_intents");
        intent.HasKey(x => x.Id);
        intent.Property(x => x.State).HasConversion<string>().HasMaxLength(20);
        intent.Property(x => x.Mode).HasMaxLength(50);
        intent.Property(x => x.SelectedReleaseGuid).HasMaxLength(500);
        intent.Property(x => x.LastFailureReason).HasMaxLength(500);
        // One persistent goal per monitored target (get-or-create keys off this).
        intent.HasIndex(x => x.TargetId).IsUnique().HasDatabaseName("ix_acquisition_intents_target_id");
        intent.HasIndex(x => x.WorkId).HasDatabaseName("ix_acquisition_intents_work_id");
        // Closing every goal a landed asset satisfied is a lookup by unit id, so it gets an index.
        // NOT unique: a season goal and its episode goals are distinct rows, and a re-created target
        // may legitimately point at the same unit again.
        intent.HasIndex(x => x.UnitId).HasDatabaseName("ix_acquisition_intents_unit_id");
        intent.HasMany(x => x.Attempts).WithOne().HasForeignKey(x => x.IntentId).OnDelete(DeleteBehavior.Cascade);
        intent.HasMany(x => x.History).WithOne().HasForeignKey(x => x.IntentId).OnDelete(DeleteBehavior.Cascade);

        var attempt = modelBuilder.Entity<AcquisitionAttempt>();
        attempt.ToTable("acquisition_attempts");
        attempt.HasKey(x => x.Id);
        attempt.Property(x => x.ReleaseGuid).HasMaxLength(500);
        attempt.Property(x => x.DownloadUrl).HasMaxLength(2048);
        attempt.Property(x => x.State).HasConversion<string>().HasMaxLength(20);
        attempt.Property(x => x.FailureReason).HasMaxLength(500);
        attempt.Property(x => x.ReleaseTitle).HasMaxLength(AcquisitionAttempt.ReleaseTitleMaxLength);
        attempt.Property(x => x.IndexerName).HasMaxLength(AcquisitionAttempt.IndexerNameMaxLength);
        attempt.HasIndex(x => x.IntentId).HasDatabaseName("ix_acquisition_attempts_intent_id");
        // A candidate is selected at most once per intent (idempotency of ReleaseSelected).
        attempt.HasIndex(x => new { x.IntentId, x.EvaluationId })
            .IsUnique()
            .HasDatabaseName("ix_acquisition_attempts_intent_evaluation");

        var attemptUnit = modelBuilder.Entity<AcquisitionAttemptUnit>();
        attemptUnit.ToTable("acquisition_attempt_units");
        attemptUnit.HasKey(x => new { x.AttemptId, x.UnitId });
        attemptUnit.HasIndex(x => x.UnitId).HasDatabaseName("ix_acquisition_attempt_units_unit_id");
        attempt.HasMany(x => x.Units).WithOne().HasForeignKey(x => x.AttemptId).OnDelete(DeleteBehavior.Cascade);

        var history = modelBuilder.Entity<AcquisitionHistoryRecord>();
        history.ToTable("acquisition_history");
        history.HasKey(x => new { x.IntentId, x.Seq });
        history.Property(x => x.FromState).HasConversion<string>().HasMaxLength(20);
        history.Property(x => x.ToState).HasConversion<string>().HasMaxLength(20);
        history.Property(x => x.Trigger).HasMaxLength(40);
        history.Property(x => x.Note).HasMaxLength(500);
    }
}
