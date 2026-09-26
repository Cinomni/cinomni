using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cinomni.Catalog.Persistence;

/// <summary>
/// EF Core context for the Catalog module. Owns the <c>catalog</c> schema;
/// no other module reads or writes its tables. Besides the works, it holds the collections they sit in
/// and the grants that decide who may browse them — one schema, so a content-access decision is one query.
/// </summary>
public sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options)
    : DbContext(options)
{
    public const string SchemaName = "catalog";

    public DbSet<Work> Works => Set<Work>();

    public DbSet<ExternalIdentifier> ExternalIdentifiers => Set<ExternalIdentifier>();

    public DbSet<Season> Seasons => Set<Season>();

    public DbSet<Episode> Episodes => Set<Episode>();

    public DbSet<Collection> Collections => Set<Collection>();

    public DbSet<CollectionGrant> Grants => Set<CollectionGrant>();

    public DbSet<StoredCollectionRule> CollectionRules => Set<StoredCollectionRule>();

    public DbSet<ImportListEntry> ImportListEntries => Set<ImportListEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(SchemaName);

        var work = modelBuilder.Entity<Work>();
        work.ToTable("works");
        work.HasKey(x => x.Id);
        // A removed work is invisible to every catalog read; only the removal itself looks past this.
        work.HasQueryFilter(x => x.RemovedAt == null);
        work.Ignore(x => x.IsRemoved);
        // Enums stored as text — stable and readable, no magic integers.
        work.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
        work.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        work.Property(x => x.Title).HasMaxLength(500);
        work.Property(x => x.SortTitle).HasMaxLength(500);
        // An empty array by default, so the column can be added NOT NULL to a populated works table.
        work.Property(x => x.Genres).HasColumnType("text[]").HasDefaultValueSql("ARRAY[]::text[]");
        work.Property(x => x.ContentRating).HasMaxLength(200);
        work.Property(x => x.Overview).HasMaxLength(8000);
        work.Property(x => x.OriginalLanguage).HasMaxLength(20);
        work.Property(x => x.PosterUrl).HasMaxLength(1000);
        work.Property(x => x.BackdropUrl).HasMaxLength(1000);
        work.Property(x => x.StructureProvider).HasMaxLength(Work.StructureProviderMaxLength);
        work.HasIndex(x => x.SortTitle).HasDatabaseName("ix_works_sort_title");
        // The catalog listing is "the visible collections, ordered by title".
        work.HasIndex(x => new { x.CollectionId, x.SortTitle }).HasDatabaseName("ix_works_collection_sort_title");

        var collection = modelBuilder.Entity<Collection>();
        collection.ToTable("collections");
        collection.HasKey(x => x.Id);
        collection.Property(x => x.Name).HasMaxLength(200);
        // Enums stored as text — stable and readable, no magic integers.
        collection.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
        collection.Property(x => x.AccessMode).HasConversion<string>().HasMaxLength(20);
        collection.HasIndex(x => x.Name).IsUnique().HasDatabaseName("ux_collections_name");
        // At most one default — where new works and the upgrade backfill land.
        collection.HasIndex(x => x.IsDefault).IsUnique().HasFilter("is_default")
            .HasDatabaseName("ux_collections_default");

        var grant = modelBuilder.Entity<CollectionGrant>();
        grant.ToTable("collection_grants");
        // One row per (collection, account): the key is what makes granting idempotent.
        grant.HasKey(x => new { x.CollectionId, x.UserId });
        // The hot lookup is "which collections may this account see".
        grant.HasIndex(x => x.UserId).HasDatabaseName("ix_collection_grants_user");
        grant.HasOne<Collection>()
            .WithMany()
            .HasForeignKey(x => x.CollectionId)
            .OnDelete(DeleteBehavior.Cascade);

        // Intra-module foreign key (same schema): a work sits in exactly one collection, and a collection
        // that still holds works cannot be deleted.
        work.HasOne<Collection>()
            .WithMany()
            .HasForeignKey(x => x.CollectionId)
            .OnDelete(DeleteBehavior.Restrict);

        MapCollectionRules(modelBuilder, work);

        MapSeriesHierarchy(modelBuilder, work);

        var external = modelBuilder.Entity<ExternalIdentifier>();
        external.ToTable("external_identifiers");
        external.HasKey(x => x.Id);
        external.Property(x => x.Provider).HasConversion<string>().HasMaxLength(20);
        external.Property(x => x.Value).HasMaxLength(200);
        external.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
        external.HasIndex(x => new { x.Provider, x.Value, x.Kind })
            .IsUnique()
            .HasDatabaseName("ux_external_identifiers_provider_value_kind");

        var list = modelBuilder.Entity<ImportListEntry>();
        list.ToTable("import_list_entries");
        list.HasKey(x => x.Id);
        list.Property(x => x.Provider).HasMaxLength(ImportListEntry.ProviderMaxLength);
        list.Property(x => x.ExternalId).HasMaxLength(ImportListEntry.ExternalIdMaxLength);
        list.Property(x => x.Kind).HasMaxLength(20);
        list.Property(x => x.Title).HasMaxLength(ImportListEntry.TitleMaxLength);
        list.Property(x => x.Outcome).HasMaxLength(ImportListEntry.OutcomeMaxLength);
        list.HasIndex(x => new { x.Provider, x.ExternalId, x.Kind })
            .IsUnique()
            .HasDatabaseName("ux_import_list_entries_identity");
        list.HasIndex(x => x.LastSeenAt).HasDatabaseName("ix_import_list_entries_last_seen");
        // Intra-module foreign key (same schema).
        work.HasMany(x => x.ExternalIdentifiers)
            .WithOne()
            .HasForeignKey(x => x.WorkId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    /// <summary>
    /// Maps the rules collections carry and the two facts a work keeps about them: whether it is pinned,
    /// and which rule placed it. Deleting a rule clears that pointer rather than blocking the delete —
    /// the sweep that follows every rule change re-places the work anyway.
    /// </summary>
    private static void MapCollectionRules(ModelBuilder modelBuilder, EntityTypeBuilder<Work> work)
    {
        var rule = modelBuilder.Entity<StoredCollectionRule>();
        rule.ToTable("collection_rules");
        rule.HasKey(x => x.Id);
        rule.Property(x => x.Name).HasMaxLength(StoredCollectionRule.NameMax);
        rule.Property(x => x.ConditionsJson).HasColumnName("conditions").HasColumnType("jsonb");
        // Every placement reads the rules of every collection in order.
        rule.HasIndex(x => new { x.CollectionId, x.Position }).HasDatabaseName("ix_collection_rules_collection_position");
        rule.HasOne<Collection>()
            .WithMany()
            .HasForeignKey(x => x.CollectionId)
            .OnDelete(DeleteBehavior.Cascade);

        work.Property(x => x.CollectionPinned).HasDefaultValue(false);
        work.HasOne<StoredCollectionRule>()
            .WithMany()
            .HasForeignKey(x => x.PlacedByRuleId)
            .OnDelete(DeleteBehavior.SetNull);
        work.HasIndex(x => x.PlacedByRuleId).HasDatabaseName("ix_works_placed_by_rule");
    }

    /// <summary>
    /// Maps <c>Work (1) → Season (N) → Episode (M)</c>. The cascade runs down the chain exactly once:
    /// <c>Episode.WorkId</c> is a plain indexed column, never a second foreign key to <c>works</c>, because
    /// two cascade paths to the same row is a PostgreSQL error.
    /// </summary>
    private static void MapSeriesHierarchy(ModelBuilder modelBuilder, EntityTypeBuilder<Work> work)
    {
        var season = modelBuilder.Entity<Season>();
        season.ToTable("seasons");
        season.HasKey(x => x.Id);
        season.Property(x => x.Title).HasMaxLength(Season.TitleMaxLength);
        season.Property(x => x.PosterUrl).HasMaxLength(Season.UrlMaxLength);
        // The natural key a structure sync upserts on.
        season.HasIndex(x => new { x.WorkId, x.Number })
            .IsUnique()
            .HasDatabaseName("ux_seasons_work_number");

        work.HasMany(x => x.Seasons)
            .WithOne()
            .HasForeignKey(x => x.WorkId)
            .OnDelete(DeleteBehavior.Cascade);

        var episode = modelBuilder.Entity<Episode>();
        episode.ToTable("episodes");
        episode.HasKey(x => x.Id);
        episode.Property(x => x.Title).HasMaxLength(Episode.TitleMaxLength);
        episode.Property(x => x.StillUrl).HasMaxLength(Episode.UrlMaxLength);
        // The natural key, and the index SxxEyy resolution reads.
        episode.HasIndex(x => new { x.WorkId, x.SeasonNumber, x.Number })
            .IsUnique()
            .HasDatabaseName("ux_episodes_work_season_number");
        // Anime absolute numbering. FILTERED on purpose: most non-anime episodes leave absolute_number
        // null, and an unfiltered unique index would reject the second such episode of a work outright.
        episode.HasIndex(x => new { x.WorkId, x.AbsoluteNumber })
            .IsUnique()
            .HasFilter("absolute_number IS NOT NULL")
            .HasDatabaseName("ux_episodes_work_absolute_number");
        // Date-based release matching (Show.2026.07.28) and the unaired sweep gate.
        episode.HasIndex(x => new { x.WorkId, x.AirDate })
            .HasDatabaseName("ix_episodes_work_air_date");

        season.HasMany(x => x.Episodes)
            .WithOne()
            .HasForeignKey(x => x.SeasonId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
