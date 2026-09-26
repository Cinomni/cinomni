using Microsoft.EntityFrameworkCore;

namespace Cinomni.Subtitles.Persistence;

/// <summary>
/// EF Core context for the Subtitles module. Owns the <c>subtitles</c> schema: the
/// searches with their scored candidates, and the obtained subtitle assets. Profiles/requirements are
/// configuration for the movie slice; providers, evaluations and upgrades are materialised when a
/// later slice needs them.
/// </summary>
public sealed class SubtitlesDbContext(DbContextOptions<SubtitlesDbContext> options)
    : DbContext(options)
{
    public const string SchemaName = "subtitles";

    public DbSet<SubtitleSearch> Searches => Set<SubtitleSearch>();

    public DbSet<SubtitleCandidate> Candidates => Set<SubtitleCandidate>();

    public DbSet<SubtitleAsset> Assets => Set<SubtitleAsset>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(SchemaName);

        var search = modelBuilder.Entity<SubtitleSearch>();
        search.ToTable("subtitle_searches");
        search.HasKey(x => x.Id);
        search.Property(x => x.State).HasConversion<string>().HasMaxLength(20);
        search.Property(x => x.Language).HasMaxLength(20);
        // One search per (asset, language, forced, hi) — the get-or-create idempotency key.
        search.HasIndex(x => new { x.AssetId, x.Language, x.Forced, x.HearingImpaired })
            .IsUnique()
            .HasDatabaseName("ux_subtitle_searches_asset_language");
        search.HasMany(x => x.Candidates).WithOne().HasForeignKey(x => x.SearchId).OnDelete(DeleteBehavior.Cascade);

        var candidate = modelBuilder.Entity<SubtitleCandidate>();
        candidate.ToTable("subtitle_candidates");
        candidate.HasKey(x => new { x.SearchId, x.Seq });
        candidate.Property(x => x.Provider).HasMaxLength(200);
        candidate.Property(x => x.Release).HasMaxLength(200);
        candidate.Property(x => x.DownloadRef).HasMaxLength(500);

        var asset = modelBuilder.Entity<SubtitleAsset>();
        asset.ToTable("subtitle_assets");
        asset.HasKey(x => x.Id);
        asset.Property(x => x.Format).HasConversion<string>().HasMaxLength(20);
        asset.Property(x => x.Language).HasMaxLength(20);
        asset.Property(x => x.Path).HasMaxLength(2048);
        asset.Property(x => x.Provider).HasMaxLength(200);
        asset.HasIndex(x => x.AssetId).HasDatabaseName("ix_subtitle_assets_asset_id");
    }
}
