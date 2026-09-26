using Microsoft.EntityFrameworkCore;

namespace Cinomni.Decision.Persistence;

/// <summary>
/// EF Core context for the Decision module. Owns the <c>decision</c> schema: the
/// acquisition profiles (qualities + custom formats) and the append-only evaluation trail.
/// </summary>
public sealed class DecisionDbContext(DbContextOptions<DecisionDbContext> options)
    : DbContext(options)
{
    public const string SchemaName = "decision";

    public DbSet<AcquisitionProfile> Profiles => Set<AcquisitionProfile>();

    public DbSet<AllowedQuality> AllowedQualities => Set<AllowedQuality>();

    public DbSet<FormatRule> FormatRules => Set<FormatRule>();

    public DbSet<FormatCondition> FormatConditions => Set<FormatCondition>();

    public DbSet<ReleaseEvaluationRecord> ReleaseEvaluations => Set<ReleaseEvaluationRecord>();

    public DbSet<DecisionReasonRecord> DecisionReasons => Set<DecisionReasonRecord>();

    public DbSet<ReleaseBlockRecord> ReleaseBlocks => Set<ReleaseBlockRecord>();

    public DbSet<TargetReleaseExclusionRecord> ReleaseExclusions => Set<TargetReleaseExclusionRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(SchemaName);

        var profile = modelBuilder.Entity<AcquisitionProfile>();
        profile.ToTable("acquisition_profiles");
        profile.HasKey(x => x.Id);
        profile.Property(x => x.Name).HasMaxLength(200);
        profile.Property(x => x.AppliesTo).HasMaxLength(20);
        profile.HasIndex(x => x.AppliesTo).HasDatabaseName("ix_acquisition_profiles_applies_to");
        profile.HasMany(x => x.AllowedQualities).WithOne().HasForeignKey(x => x.ProfileId).OnDelete(DeleteBehavior.Cascade);
        profile.HasMany(x => x.FormatRules).WithOne().HasForeignKey(x => x.ProfileId).OnDelete(DeleteBehavior.Cascade);

        var quality = modelBuilder.Entity<AllowedQuality>();
        quality.ToTable("allowed_qualities");
        quality.HasKey(x => x.Id);
        quality.Property(x => x.Source).HasConversion<string>().HasMaxLength(20);
        quality.Property(x => x.Resolution).HasConversion<string>().HasMaxLength(20);
        quality.HasIndex(x => x.ProfileId).HasDatabaseName("ix_allowed_qualities_profile_id");

        var rule = modelBuilder.Entity<FormatRule>();
        rule.ToTable("format_rules");
        rule.HasKey(x => x.Id);
        rule.Property(x => x.Name).HasMaxLength(200);
        rule.HasMany(x => x.Conditions).WithOne().HasForeignKey(x => x.RuleId).OnDelete(DeleteBehavior.Cascade);
        rule.HasIndex(x => x.ProfileId).HasDatabaseName("ix_format_rules_profile_id");

        var condition = modelBuilder.Entity<FormatCondition>();
        condition.ToTable("format_conditions");
        condition.HasKey(x => x.Id);
        condition.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
        condition.Property(x => x.Value).HasMaxLength(500);
        condition.HasIndex(x => x.RuleId).HasDatabaseName("ix_format_conditions_rule_id");

        var evaluation = modelBuilder.Entity<ReleaseEvaluationRecord>();
        evaluation.ToTable("release_evaluations");
        evaluation.HasKey(x => x.Id);
        evaluation.Property(x => x.ReleaseGuid).HasMaxLength(500);
        evaluation.Property(x => x.ReleaseTitle).HasMaxLength(1000);
        evaluation.Property(x => x.CanonicalKey).HasMaxLength(500);
        evaluation.Property(x => x.Verdict).HasConversion<string>().HasMaxLength(20);
        evaluation.Property(x => x.EvaluatorVersion).HasMaxLength(20);
        evaluation.HasIndex(x => x.SearchId).HasDatabaseName("ix_release_evaluations_search_id");
        evaluation.HasIndex(x => x.TargetId).HasDatabaseName("ix_release_evaluations_target_id");
        // Drives the retention sweep, which walks (verdict, created_at) over the largest table here.
        evaluation.HasIndex(x => new { x.Verdict, x.CreatedAt })
            .HasDatabaseName("ix_release_evaluations_purge");
        evaluation.HasMany(x => x.Reasons).WithOne().HasForeignKey(x => x.EvaluationId).OnDelete(DeleteBehavior.Cascade);

        var reason = modelBuilder.Entity<DecisionReasonRecord>();
        reason.ToTable("decision_reasons");
        reason.HasKey(x => new { x.EvaluationId, x.Seq });
        reason.Property(x => x.Rule).HasMaxLength(100);
        reason.Property(x => x.Property).HasMaxLength(50);
        reason.Property(x => x.ProfileValue).HasMaxLength(200);
        reason.Property(x => x.ActualValue).HasMaxLength(200);
        reason.Property(x => x.Outcome).HasConversion<string>().HasMaxLength(10);
        reason.Property(x => x.Rejection).HasConversion<string>().HasMaxLength(10);

        // Never purged. A block is why the sweep left a release alone, and that answer has to survive
        // the evaluation trail being aged out.
        var block = modelBuilder.Entity<ReleaseBlockRecord>();
        block.ToTable("release_blocks");
        block.HasKey(x => x.Id);
        block.Property(x => x.ReleaseGuid).HasMaxLength(500);
        block.Property(x => x.ReleaseTitle).HasMaxLength(1000);
        block.Property(x => x.Reason).HasMaxLength(500);
        block.HasIndex(x => x.ReleaseGuid).IsUnique().HasDatabaseName("ix_release_blocks_release_guid");

        var exclusion = modelBuilder.Entity<TargetReleaseExclusionRecord>();
        exclusion.ToTable("target_release_exclusions");
        exclusion.HasKey(x => x.Id);
        exclusion.Property(x => x.ReleaseGuid).HasMaxLength(500);
        exclusion.Property(x => x.Reason).HasMaxLength(500);
        // One per goal and release, and the sweep reads them by goal.
        exclusion.HasIndex(x => new { x.TargetId, x.ReleaseGuid })
            .IsUnique()
            .HasDatabaseName("ux_target_release_exclusions_target_release");
    }
}
