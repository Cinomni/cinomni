using Microsoft.EntityFrameworkCore;

namespace Cinomni.Playback.Persistence;

/// <summary>
/// EF Core context for the Playback module. Owns the <c>playback</c> schema: the
/// persisted sessions with their explainable plan (jsonb) and transcode jobs, plus the per-(user,asset)
/// resume progress. Device/capability profiles collapse into the request for the movie slice; the
/// stream-selection is folded onto the session.
/// </summary>
public sealed class PlaybackDbContext(DbContextOptions<PlaybackDbContext> options)
    : DbContext(options)
{
    public const string SchemaName = "playback";

    public DbSet<PlaybackSession> Sessions => Set<PlaybackSession>();

    public DbSet<TranscodeJob> TranscodeJobs => Set<TranscodeJob>();

    public DbSet<PlaybackProgress> Progress => Set<PlaybackProgress>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(SchemaName);

        var session = modelBuilder.Entity<PlaybackSession>();
        session.ToTable("playback_sessions");
        session.HasKey(x => x.Id);
        session.Property(x => x.State).HasConversion<string>().HasMaxLength(20);
        session.Property(x => x.EndReason).HasConversion<string>().HasMaxLength(20);
        session.Property(x => x.Method).HasConversion<string>().HasMaxLength(20);
        session.Property(x => x.FullPath).HasMaxLength(2048);
        session.Property(x => x.Container).HasMaxLength(80);
        session.Property(x => x.PlaySessionId).HasMaxLength(100);
        session.Property(x => x.PlanJson).HasColumnType("jsonb");
        session.Property(x => x.Version).IsConcurrencyToken();
        session.Property(x => x.WorkId);
        session.HasIndex(x => new { x.UserId, x.AssetId }).HasDatabaseName("ix_playback_sessions_user_asset");
        // Drives the retention sweep over the sessions that already finished.
        session.HasIndex(x => new { x.State, x.UpdatedAt }).HasDatabaseName("ix_playback_sessions_purge");
        session.HasMany(x => x.TranscodeJobs).WithOne().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);

        var transcode = modelBuilder.Entity<TranscodeJob>();
        transcode.ToTable("transcode_jobs");
        transcode.HasKey(x => x.Id);
        transcode.Property(x => x.State).HasConversion<string>().HasMaxLength(20);
        transcode.Property(x => x.TargetVideoCodec).HasMaxLength(40);
        transcode.Property(x => x.TargetAudioCodec).HasMaxLength(40);
        transcode.Property(x => x.OutputPath).HasMaxLength(2048);
        transcode.Property(x => x.LastError).HasMaxLength(500);
        transcode.Property(x => x.FallbackReason).HasMaxLength(500);
        // The sweep asks every 30 seconds which jobs still name a running process.
        transcode.HasIndex(x => x.State).HasDatabaseName("ix_transcode_jobs_state");

        var progress = modelBuilder.Entity<PlaybackProgress>();
        progress.ToTable("playback_progress");
        // One upserted row per (user, asset) — state, not history. The key is
        // deliberately unchanged by the series slice: work_id/unit_id are correlation, not identity.
        progress.HasKey(x => new { x.UserId, x.AssetId });
        // "Continue watching" and a whole-season watched-flag lookup are both (user, work) scans.
        progress.HasIndex(x => new { x.UserId, x.WorkId }).HasDatabaseName("ix_playback_progress_user_work");
    }
}
