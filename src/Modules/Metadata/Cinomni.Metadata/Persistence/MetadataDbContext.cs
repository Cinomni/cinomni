using Microsoft.EntityFrameworkCore;

namespace Cinomni.Metadata.Persistence;

/// <summary>
/// EF Core context for the Metadata module. Owns the <c>metadata</c> schema: the neutral
/// snapshots obtained from providers, their artwork candidates, and the per-work refresh state. Provider
/// configuration and credentials are options/secrets for the movie slice, not tables.
/// </summary>
public sealed class MetadataDbContext(DbContextOptions<MetadataDbContext> options)
    : DbContext(options)
{
    public const string SchemaName = "metadata";

    public DbSet<MetadataSnapshotRecord> Snapshots => Set<MetadataSnapshotRecord>();

    public DbSet<MetadataArtworkRecord> Artwork => Set<MetadataArtworkRecord>();

    public DbSet<MetadataSeasonRecord> Seasons => Set<MetadataSeasonRecord>();

    public DbSet<MetadataEpisodeRecord> Episodes => Set<MetadataEpisodeRecord>();

    public DbSet<RefreshState> RefreshStates => Set<RefreshState>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(SchemaName);

        var snapshot = modelBuilder.Entity<MetadataSnapshotRecord>();
        snapshot.ToTable("metadata_snapshots");
        snapshot.HasKey(x => x.Id);
        snapshot.Property(x => x.Provider).HasMaxLength(200);
        // Enums stored as text — stable and readable, no magic integers.
        snapshot.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
        snapshot.Property(x => x.ExternalId).HasMaxLength(200);
        snapshot.Property(x => x.Title).HasMaxLength(1000);
        snapshot.Property(x => x.OriginalTitle).HasMaxLength(1000);
        snapshot.Property(x => x.OriginalLanguage).HasMaxLength(200);
        // text[] rather than a child table: a genre here is a label this provider used, not an entity
        // this system owns, and a table would invite reconciling two providers' vocabularies as if
        // that had one right answer.
        // An empty array by default, so the column can be added NOT NULL to a table that already holds
        // every snapshot this installation has ever taken. Without it the migration would refuse to
        // apply to a populated installation, which is the only kind that matters.
        snapshot.Property(x => x.Genres).HasColumnType("text[]").HasDefaultValueSql("ARRAY[]::text[]");
        snapshot.Property(x => x.ContentRating).HasMaxLength(200);
        snapshot.Property(x => x.PosterUrl).HasMaxLength(1000);
        snapshot.Property(x => x.BackdropUrl).HasMaxLength(1000);
        snapshot.Property(x => x.SeriesStatus).HasConversion<string>().HasMaxLength(20);
        snapshot.Property(x => x.TvdbId).HasMaxLength(200);
        snapshot.Property(x => x.ImdbId).HasMaxLength(200);
        snapshot.Property(x => x.TmdbId).HasMaxLength(200);
        snapshot.Property(x => x.SeasonOrder).HasMaxLength(200);
        // The untouched provider response, kept as a controlled JSONB blob.
        snapshot.Property(x => x.RawResponse).HasColumnType("jsonb");
        snapshot.HasIndex(x => x.WorkId).HasDatabaseName("ix_metadata_snapshots_work_id");
        // Drives the retention sweep, which walks the snapshots by age.
        snapshot.HasIndex(x => x.FetchedAt).HasDatabaseName("ix_metadata_snapshots_fetched_at");

        var artwork = modelBuilder.Entity<MetadataArtworkRecord>();
        artwork.ToTable("metadata_artwork");
        artwork.HasKey(x => x.Id);
        artwork.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
        artwork.Property(x => x.Url).HasMaxLength(1000);
        artwork.Property(x => x.Language).HasMaxLength(20);
        artwork.HasIndex(x => x.SnapshotId).HasDatabaseName("ix_metadata_artwork_snapshot_id");
        // Artwork rows live and die with their snapshot (same schema, intra-module FK).
        snapshot.HasMany(x => x.Artwork)
            .WithOne()
            .HasForeignKey(x => x.SnapshotId)
            .OnDelete(DeleteBehavior.Cascade);

        var season = modelBuilder.Entity<MetadataSeasonRecord>();
        season.ToTable("metadata_seasons");
        season.HasKey(x => x.Id);
        season.Property(x => x.Title).HasMaxLength(500);
        season.Property(x => x.PosterUrl).HasMaxLength(1000);
        season.Property(x => x.ExternalId).HasMaxLength(200);
        // The season number is the natural key inside a snapshot — a provider may not list one twice.
        season.HasIndex(x => new { x.SnapshotId, x.Number })
            .IsUnique()
            .HasDatabaseName("ux_metadata_seasons_snapshot_number");
        snapshot.HasMany(x => x.Seasons)
            .WithOne()
            .HasForeignKey(x => x.SnapshotId)
            .OnDelete(DeleteBehavior.Cascade);

        var episode = modelBuilder.Entity<MetadataEpisodeRecord>();
        episode.ToTable("metadata_episodes");
        episode.HasKey(x => x.Id);
        episode.Property(x => x.Title).HasMaxLength(1000);
        episode.Property(x => x.StillUrl).HasMaxLength(1000);
        episode.Property(x => x.ExternalId).HasMaxLength(200);
        // (season, episode) is the natural key inside a snapshot; it is also the read order.
        episode.HasIndex(x => new { x.SnapshotId, x.SeasonNumber, x.Number })
            .IsUnique()
            .HasDatabaseName("ux_metadata_episodes_snapshot_season_number");
        // Absolute numbering is anime-only, so the lookup index is filtered rather than unique.
        episode.HasIndex(x => new { x.SnapshotId, x.AbsoluteNumber })
            .HasDatabaseName("ix_metadata_episodes_snapshot_absolute")
            .HasFilter("absolute_number IS NOT NULL");
        // Date-based release matching and the unaired gate both sweep on the air date.
        episode.HasIndex(x => x.AirDate).HasDatabaseName("ix_metadata_episodes_air_date");
        snapshot.HasMany(x => x.Episodes)
            .WithOne()
            .HasForeignKey(x => x.SnapshotId)
            .OnDelete(DeleteBehavior.Cascade);

        var refresh = modelBuilder.Entity<RefreshState>();
        refresh.ToTable("refresh_states");
        refresh.HasKey(x => x.Id);
        refresh.Property(x => x.Provider).HasMaxLength(200);
        refresh.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        // One refresh state per (work, provider) — the get-or-create idempotency key.
        refresh.HasIndex(x => new { x.WorkId, x.Provider })
            .IsUnique()
            .HasDatabaseName("ux_refresh_states_work_provider");
    }
}
