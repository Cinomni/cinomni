using Microsoft.EntityFrameworkCore;

namespace Cinomni.Import.Persistence;

/// <summary>
/// EF Core context for the Import module. Owns the <c>import</c> schema: the
/// persisted, recoverable import jobs plus their <b>per-file matches</b>, file operations and
/// append-only history trail. The <c>file_matches</c> table is materialised by the series
/// slice, because a season pack lands many files under one job and every per-file value — target
/// path, asset id, units, media info — has to live somewhere other than the root.
/// </summary>
public sealed class ImportDbContext(DbContextOptions<ImportDbContext> options)
    : DbContext(options)
{
    public const string SchemaName = "import";

    public DbSet<ImportJob> Jobs => Set<ImportJob>();

    public DbSet<ImportFileMatch> Matches => Set<ImportFileMatch>();

    public DbSet<FileOperation> Operations => Set<FileOperation>();

    public DbSet<ImportHistoryRecord> History => Set<ImportHistoryRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(SchemaName);

        var job = modelBuilder.Entity<ImportJob>();
        job.ToTable("import_jobs");
        job.HasKey(x => x.Id);
        job.Property(x => x.State).HasConversion<string>().HasMaxLength(20);
        job.Property(x => x.SourcePath).HasMaxLength(2048);
        job.Property(x => x.MatchedFilePath).HasMaxLength(2048);
        job.Property(x => x.MatchedFileHash).HasMaxLength(128);
        job.Property(x => x.TargetPath).HasMaxLength(2048);
        job.Property(x => x.Reason).HasMaxLength(500);
        job.Property(x => x.MediaInfoJson).HasColumnType("jsonb");
        job.Property(x => x.RequestedUnitsJson).HasColumnType("jsonb");
        job.Ignore(x => x.RequestedUnitIds);
        job.Ignore(x => x.HasUnfinishedWork);
        // One import per completed download (idempotency of DownloadCompleted: get-or-create keys here).
        job.HasIndex(x => x.DownloadTaskId).IsUnique().HasDatabaseName("ix_import_jobs_download_task_id");
        job.HasIndex(x => x.IntentId).HasDatabaseName("ix_import_jobs_intent_id");
        // Pending-job lookups skip the terminal states (partial index).
        job.HasIndex(x => x.State).HasDatabaseName("ix_import_jobs_state");
        job.HasMany(x => x.Matches).WithOne().HasForeignKey(x => x.ImportJobId).OnDelete(DeleteBehavior.Cascade);
        job.HasMany(x => x.Operations).WithOne().HasForeignKey(x => x.ImportJobId).OnDelete(DeleteBehavior.Cascade);
        job.HasMany(x => x.History).WithOne().HasForeignKey(x => x.ImportJobId).OnDelete(DeleteBehavior.Cascade);

        var match = modelBuilder.Entity<ImportFileMatch>();
        match.ToTable("import_file_matches");
        match.HasKey(x => new { x.ImportJobId, x.Seq });
        match.Property(x => x.State).HasConversion<string>().HasMaxLength(20);
        match.Property(x => x.SourcePath).HasMaxLength(2048);
        match.Property(x => x.Hash).HasMaxLength(128);
        match.Property(x => x.TargetPath).HasMaxLength(2048);
        match.Property(x => x.Reason).HasMaxLength(500);
        match.Property(x => x.MediaInfoJson).HasColumnType("jsonb");
        // The asset id is the join into library.media_assets; unique because a match mints exactly one
        // asset id and reuses it forever — two matches sharing one would break the retry guarantee.
        match.HasIndex(x => x.AssetId).IsUnique().HasDatabaseName("ux_import_file_matches_asset_id");

        var operation = modelBuilder.Entity<FileOperation>();
        operation.ToTable("file_operations");
        operation.HasKey(x => new { x.ImportJobId, x.Seq });
        operation.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
        operation.Property(x => x.State).HasConversion<string>().HasMaxLength(20);
        operation.Property(x => x.FromPath).HasMaxLength(2048);
        operation.Property(x => x.ToPath).HasMaxLength(2048);

        var history = modelBuilder.Entity<ImportHistoryRecord>();
        history.ToTable("import_history");
        history.HasKey(x => new { x.ImportJobId, x.Seq });
        history.Property(x => x.FromState).HasConversion<string>().HasMaxLength(20);
        history.Property(x => x.ToState).HasConversion<string>().HasMaxLength(20);
        history.Property(x => x.Trigger).HasMaxLength(40);
        history.Property(x => x.Note).HasMaxLength(500);
    }
}
