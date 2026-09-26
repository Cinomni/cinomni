using Microsoft.EntityFrameworkCore;

namespace Cinomni.Monitoring.Persistence;

/// <summary>
/// EF Core context for the Monitoring module. Owns the <c>monitoring</c> schema;
/// no other module reads or writes its tables — cross-context links are by id.
/// </summary>
public sealed class MonitoringDbContext(DbContextOptions<MonitoringDbContext> options)
    : DbContext(options)
{
    public const string SchemaName = "monitoring";

    public DbSet<MonitoredTarget> MonitoredTargets => Set<MonitoredTarget>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(SchemaName);

        var target = modelBuilder.Entity<MonitoredTarget>();
        target.ToTable("monitored_targets");
        target.HasKey(x => x.Id);
        // Enums stored as text — stable and readable, no magic integers. Appending a member is safe;
        // renaming one corrupts every existing row.
        target.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
        target.Property(x => x.Mode).HasConversion<string>().HasMaxLength(20);
        target.Property(x => x.EpisodeTitle).HasMaxLength(MonitoredTarget.TitleMaxLength);
        target.Ignore(x => x.IsRoot);
        target.Ignore(x => x.IsAcquirable);

        // One target per catalog unit. This replaces the movie-era unique index on work_id alone,
        // which the hierarchy makes impossible (one work now holds a root, N seasons and M episodes).
        // It is still the concurrency guard MonitoringCommands relies on: two concurrent applies race
        // on this constraint and the loser converges on the winner's row instead of duplicating it.
        // Existing movie rows stay unique under it as (work_id, 'Movie', work_id).
        target.HasIndex(x => new { x.WorkId, x.Kind, x.TargetRef })
            .IsUnique()
            .HasDatabaseName("ux_monitored_targets_work_ref");

        // Every "the tree of this work" read; no longer unique, so it needs its own index.
        target.HasIndex(x => x.WorkId)
            .HasDatabaseName("ix_monitored_targets_work_id");

        // Walking down the tree (a season's episodes, a root's seasons) and rolling missing state up.
        target.HasIndex(x => x.ParentTargetId)
            .HasDatabaseName("ix_monitored_targets_parent");

        // Hot query of the missing-search job: monitored targets that still lack an asset, with the
        // air instant along for the ride so the unaired gate is served by the same index.
        target.HasIndex(x => new { x.Monitored, x.IsMissing, x.AirDate })
            .HasDatabaseName("ix_monitored_targets_monitored_missing");

        // The calendar reads a date window. Without this, every page scans the target table.
        target.HasIndex(x => x.PublishedAirDate)
            .HasDatabaseName("ix_monitored_targets_published_air_date");
    }
}
