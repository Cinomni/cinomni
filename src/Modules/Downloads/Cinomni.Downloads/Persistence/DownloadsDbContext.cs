using Microsoft.EntityFrameworkCore;

namespace Cinomni.Downloads.Persistence;

/// <summary>
/// EF Core context for the Downloads module. Owns the <c>downloads</c> schema: the
/// persisted download tasks plus their file layout and append-only history trail. The task is kept
/// in the database (not in memory) so it survives a restart and is reconciled against the sidecar
/// from its resume-data checkpoint (cases #9/#13/#14). TorrentTracker/TorrentPeerSummary and the
/// bandwidth policy from the design are deferred; the seeding policy folds into a jsonb column.
/// </summary>
public sealed class DownloadsDbContext(DbContextOptions<DownloadsDbContext> options)
    : DbContext(options)
{
    public const string SchemaName = "downloads";

    public DbSet<DownloadTask> Tasks => Set<DownloadTask>();

    public DbSet<TorrentFile> Files => Set<TorrentFile>();

    public DbSet<DownloadTaskUnit> Units => Set<DownloadTaskUnit>();

    public DbSet<DownloadTaskClaim> Claims => Set<DownloadTaskClaim>();

    public DbSet<DownloadHistoryRecord> History => Set<DownloadHistoryRecord>();

    /// <summary>The single row describing where torrent traffic is currently observed to be going.</summary>
    public DbSet<TunnelStateRecord> TunnelState => Set<TunnelStateRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(SchemaName);

        var task = modelBuilder.Entity<DownloadTask>();
        task.ToTable("download_tasks");
        task.HasKey(x => x.Id);
        task.Property(x => x.State).HasConversion<string>().HasMaxLength(20);
        task.Property(x => x.ReleaseGuid).HasMaxLength(2048);
        task.Property(x => x.DownloadUrl).HasMaxLength(2048);
        task.Property(x => x.SavePath).HasMaxLength(2048);
        task.Property(x => x.InfoHash).HasMaxLength(64);
        task.Property(x => x.Name).HasMaxLength(500);
        task.Property(x => x.ContentPath).HasMaxLength(2048);
        task.Property(x => x.LastError).HasMaxLength(500);
        task.Property(x => x.SeedingPolicyJson).HasColumnType("jsonb");
        task.Property(x => x.ResumeData).HasColumnType("bytea");
        task.Property(x => x.NetworkHoldReason).HasMaxLength(500);
        task.Ignore(x => x.IsNetworkHeld);
        // Finding what to release when the tunnel comes back, and counting what is held for the
        // operator's view. Partial on the hold itself: outside an outage the index holds no rows at
        // all, which is the state an installation is in almost all of the time.
        task.HasIndex(x => x.NetworkHoldSince)
            .HasDatabaseName("ix_download_tasks_network_hold")
            .HasFilter("network_hold_since IS NOT NULL");
        // One download per acquisition attempt (idempotency of DownloadQueued: get-or-create keys here).
        task.HasIndex(x => x.AttemptId).IsUnique().HasDatabaseName("ix_download_tasks_attempt_id");
        task.HasIndex(x => x.IntentId).HasDatabaseName("ix_download_tasks_intent_id");
        // Reconciling the status stream and restarts looks tasks up by their sidecar info-hash.
        // Deliberately NOT unique: a torrent legitimately reappears after an earlier task for it was
        // completed or removed. At most one task per hash is *in flight* at a time, and that is
        // enforced by DownloadService.AddDownloadAsync rather than by a partial unique index — an
        // index would fail to build on an installation that already contains the duplicates this
        // change exists to prevent.
        task.HasIndex(x => x.InfoHash).HasDatabaseName("ix_download_tasks_info_hash");
        // Drives the checkpoint trim. Partial on the blob's presence, so the index is tiny: only the
        // handful of tasks still carrying resume data are ever candidates.
        task.HasIndex(x => new { x.State, x.UpdatedAt })
            .HasDatabaseName("ix_download_tasks_checkpoint_trim")
            .HasFilter("resume_data IS NOT NULL");
        task.HasMany(x => x.Files).WithOne().HasForeignKey(x => x.DownloadTaskId).OnDelete(DeleteBehavior.Cascade);
        task.HasMany(x => x.History).WithOne().HasForeignKey(x => x.DownloadTaskId).OnDelete(DeleteBehavior.Cascade);
        task.HasMany(x => x.Units).WithOne().HasForeignKey(x => x.DownloadTaskId).OnDelete(DeleteBehavior.Cascade);
        task.HasMany(x => x.Claims).WithOne().HasForeignKey(x => x.DownloadTaskId).OnDelete(DeleteBehavior.Cascade);

        var unit = modelBuilder.Entity<DownloadTaskUnit>();
        unit.ToTable("download_task_units");
        unit.HasKey(x => new { x.DownloadTaskId, x.UnitId });
        unit.HasIndex(x => x.UnitId).HasDatabaseName("ix_download_task_units_unit_id");

        var claim = modelBuilder.Entity<DownloadTaskClaim>();
        claim.ToTable("download_task_claims");
        claim.HasKey(x => new { x.DownloadTaskId, x.AttemptId });
        // One attempt waits on at most one download, whichever task it ended up attached to. This is
        // the dedup key an add checks, taking over from the task's own unique attempt_id index.
        claim.HasIndex(x => x.AttemptId).IsUnique().HasDatabaseName("ux_download_task_claims_attempt_id");
        claim.HasIndex(x => x.IntentId).HasDatabaseName("ix_download_task_claims_intent_id");

        var file = modelBuilder.Entity<TorrentFile>();
        file.ToTable("torrent_files");
        file.HasKey(x => new { x.DownloadTaskId, x.Index });
        file.Property(x => x.Path).HasMaxLength(2048);
        file.Property(x => x.Priority).HasConversion<string>().HasMaxLength(20);

        var tunnel = modelBuilder.Entity<TunnelStateRecord>();
        tunnel.ToTable("tunnel_state");
        tunnel.HasKey(x => x.Id);
        tunnel.Property(x => x.Reason).HasMaxLength(200);
        tunnel.Property(x => x.TunnelDevice).HasMaxLength(64);

        var history = modelBuilder.Entity<DownloadHistoryRecord>();
        history.ToTable("download_history");
        history.HasKey(x => new { x.DownloadTaskId, x.Seq });
        history.Property(x => x.FromState).HasConversion<string>().HasMaxLength(20);
        history.Property(x => x.ToState).HasConversion<string>().HasMaxLength(20);
        history.Property(x => x.Trigger).HasMaxLength(40);
        history.Property(x => x.Note).HasMaxLength(500);
    }
}
