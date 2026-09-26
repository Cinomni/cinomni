using Microsoft.EntityFrameworkCore;

namespace Cinomni.Library.Persistence;

/// <summary>
/// EF Core context for the Library module. Owns the <c>library</c> schema: the media
/// assets, their versions, and the <b>relational</b> streams (a queryable row per track,
/// not a JSON blob), plus the N:M asset↔target links. <c>MediaAttachment</c> and the library-scan
/// bookkeeping are materialised when a later slice needs them.
/// </summary>
public sealed class LibraryDbContext(DbContextOptions<LibraryDbContext> options)
    : DbContext(options)
{
    public const string SchemaName = "library";

    public DbSet<MediaAsset> Assets => Set<MediaAsset>();

    public DbSet<MediaVersion> Versions => Set<MediaVersion>();

    public DbSet<MediaStream> Streams => Set<MediaStream>();

    public DbSet<AssetTargetLink> TargetLinks => Set<AssetTargetLink>();

    public DbSet<AssetUnitLink> UnitLinks => Set<AssetUnitLink>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(SchemaName);

        var asset = modelBuilder.Entity<MediaAsset>();
        asset.ToTable("media_assets");
        asset.HasKey(x => x.Id);
        asset.Property(x => x.State).HasConversion<string>().HasMaxLength(20);
        asset.Property(x => x.Version).IsConcurrencyToken();
        asset.HasIndex(x => x.WorkId).HasDatabaseName("ix_media_assets_work_id");
        asset.HasMany(x => x.Versions).WithOne().HasForeignKey(x => x.AssetId).OnDelete(DeleteBehavior.Cascade);
        asset.HasMany(x => x.TargetLinks).WithOne().HasForeignKey(x => x.AssetId).OnDelete(DeleteBehavior.Cascade);
        asset.HasMany(x => x.UnitLinks).WithOne().HasForeignKey(x => x.AssetId).OnDelete(DeleteBehavior.Cascade);

        var version = modelBuilder.Entity<MediaVersion>();
        version.ToTable("media_versions");
        version.HasKey(x => x.Id);
        version.Property(x => x.RelativePath).HasMaxLength(2048);
        version.Property(x => x.FullPath).HasMaxLength(2048);
        version.Property(x => x.QualityJson).HasColumnType("jsonb");
        version.Property(x => x.ReleaseGroup).HasMaxLength(200);
        // One live version per file on disk (scan/dedup key). Retired versions are left out: an upgrade
        // lands on the very path the version it replaces names, and both rows have to be kept.
        version.HasIndex(x => x.FullPath)
            .IsUnique()
            .HasFilter("retired_at IS NULL")
            .HasDatabaseName("ux_media_versions_full_path");
        version.HasMany(x => x.Streams).WithOne().HasForeignKey(x => x.MediaVersionId).OnDelete(DeleteBehavior.Cascade);

        var stream = modelBuilder.Entity<MediaStream>();
        stream.ToTable("media_streams");
        stream.HasKey(x => x.Id);
        stream.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
        stream.Property(x => x.VideoRangeType).HasConversion<string>().HasMaxLength(20);
        stream.Property(x => x.Codec).HasMaxLength(80);
        stream.Property(x => x.Language).HasMaxLength(40);
        // Relational streams are queried by track slot and filtered by kind.
        stream.HasIndex(x => new { x.MediaVersionId, x.StreamIndex })
            .IsUnique()
            .HasDatabaseName("ux_media_streams_version_index");
        stream.HasIndex(x => x.Type).HasDatabaseName("ix_media_streams_type");

        // Left byte-identical on purpose: it keeps holding the acquiring monitored target, and the
        // asset↔unit relation below is added alongside it rather than reinterpreting it.
        var link = modelBuilder.Entity<AssetTargetLink>();
        link.ToTable("asset_target_links");
        link.HasKey(x => new { x.AssetId, x.TargetId });
        link.HasIndex(x => x.TargetId).HasDatabaseName("ix_asset_target_links_target_id");

        var unitLink = modelBuilder.Entity<AssetUnitLink>();
        unitLink.ToTable("asset_unit_links");
        unitLink.HasKey(x => new { x.AssetId, x.UnitId });
        // "Which file plays episode X" is the per-unit lookup the series UI and Playback make.
        unitLink.HasIndex(x => x.UnitId).HasDatabaseName("ix_asset_unit_links_unit_id");
    }
}
