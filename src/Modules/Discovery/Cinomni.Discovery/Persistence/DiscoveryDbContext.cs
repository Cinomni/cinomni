using Cinomni.Discovery.Indexers.Definition;
using Microsoft.EntityFrameworkCore;

namespace Cinomni.Discovery.Persistence;

/// <summary>
/// EF Core context for the Discovery module. Owns the <c>discovery</c> schema;
/// no other module reads or writes its tables — downstream modules pull raw results through
/// <c>IReleaseSearchResults</c>.
/// </summary>
public sealed class DiscoveryDbContext(DbContextOptions<DiscoveryDbContext> options)
    : DbContext(options)
{
    public const string SchemaName = "discovery";

    public DbSet<Indexer> Indexers => Set<Indexer>();

    public DbSet<IndexerDefinition> IndexerDefinitions => Set<IndexerDefinition>();

    public DbSet<IndexerSession> IndexerSessions => Set<IndexerSession>();

    public DbSet<IndexerCatalogSource> IndexerCatalogSources => Set<IndexerCatalogSource>();

    public DbSet<IndexerCatalogSourceEntry> IndexerCatalogEntries => Set<IndexerCatalogSourceEntry>();

    public DbSet<IndexerUsage> IndexerUsages => Set<IndexerUsage>();

    public DbSet<SearchExecution> SearchExecutions => Set<SearchExecution>();

    public DbSet<SearchResult> SearchResults => Set<SearchResult>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(SchemaName);

        var indexer = modelBuilder.Entity<Indexer>();
        // The module's first check constraint, and it earns being one: the cipher is useless without
        // its nonce, so a row carrying one and not the other is a credential that can never be read
        // back. Code writes the four credential fields together (Indexer.SetCredential); this is what
        // holds when something else writes the row.
        indexer.ToTable("indexers");
        indexer.HasKey(x => x.Id);
        indexer.Property(x => x.Protocol).HasConversion<string>().HasMaxLength(20);
        indexer.Property(x => x.Name).HasMaxLength(Indexer.NameMaxLength);
        indexer.Property(x => x.BaseUrl).HasMaxLength(Indexer.BaseUrlMaxLength);
        indexer.Property(x => x.MovieCategories).HasMaxLength(Indexer.CapabilityListMaxLength);
        indexer.Property(x => x.TvCategories).HasMaxLength(Indexer.CapabilityListMaxLength);
        indexer.Property(x => x.MovieSearchParams).HasMaxLength(Indexer.CapabilityListMaxLength);
        indexer.Property(x => x.TvSearchParams).HasMaxLength(Indexer.CapabilityListMaxLength);
        indexer.Property(x => x.CredentialUsername).HasMaxLength(Indexer.CredentialUsernameMaxLength);
        indexer.Property(x => x.SecretKeyId).HasMaxLength(32);
        indexer.Property(x => x.CatalogKey).HasMaxLength(50);
        indexer.Property(x => x.LimitsUnit).HasConversion<string>().HasMaxLength(10)
            .HasDefaultValue(Contracts.IndexerLimitsUnit.Day);
        indexer.Property(x => x.LastTestCode).HasMaxLength(100);
        indexer.Property(x => x.LastTestMessage).HasMaxLength(500);
        // Install idempotency: one indexer per (source, key). A null source (installed from the
        // catalog earlier versions shipped, or from a source since removed) is not constrained, which
        // PostgreSQL's distinct NULLs give for free.
        indexer.HasIndex(x => new { x.CatalogSourceId, x.CatalogKey }).IsUnique();
        indexer.HasOne<IndexerCatalogSource>()
            .WithMany()
            .HasForeignKey(x => x.CatalogSourceId)
            .OnDelete(DeleteBehavior.SetNull);
        indexer.ToTable("indexers", t =>
        {
            t.HasCheckConstraint("ck_indexers_secret_cipher_with_nonce", "(secret_cipher IS NULL) = (secret_nonce IS NULL)");
            t.HasCheckConstraint("ck_indexers_minimum_seeders", "minimum_seeders IS NULL OR minimum_seeders >= 0");
            t.HasCheckConstraint("ck_indexers_query_limit", "query_limit IS NULL OR query_limit > 0");
            t.HasCheckConstraint("ck_indexers_grab_limit", "grab_limit IS NULL OR grab_limit > 0");
        });
        indexer.HasIndex(x => new { x.Enabled, x.Priority })
            .HasDatabaseName("ix_indexers_enabled_priority");
        indexer.HasOne<IndexerDefinition>()
            .WithMany()
            .HasForeignKey(x => x.DefinitionId)
            .OnDelete(DeleteBehavior.Restrict);

        var definition = modelBuilder.Entity<IndexerDefinition>();
        definition.ToTable("indexer_definitions");
        definition.HasKey(x => x.Id);
        definition.Property(x => x.Name).HasMaxLength(IndexerDefinition.NameMaxLength);
        definition.Property(x => x.ContentHash).HasMaxLength(64);
        definition.Property(x => x.RawContent).HasMaxLength(IndexerDefinitionParser.MaxRawContentLength);

        var source = modelBuilder.Entity<IndexerCatalogSource>();
        source.ToTable("indexer_catalog_sources");
        source.HasKey(x => x.Id);
        source.Property(x => x.Name).HasMaxLength(IndexerCatalogSource.NameMaxLength);
        source.Property(x => x.Url).HasMaxLength(IndexerCatalogSource.UrlMaxLength);
        source.Property(x => x.LastRefreshCode).HasMaxLength(IndexerCatalogSource.RefreshCodeMaxLength);
        source.Property(x => x.LastRefreshMessage).HasMaxLength(IndexerCatalogSource.RefreshMessageMaxLength);
        source.HasIndex(x => x.Url).IsUnique();

        // The last successfully fetched manifest, relational rather than one JSON blob: install and the
        // settings check look an entry up by (source, key), and a refresh replaces the set in one
        // transaction under a lock on the source row.
        var entry = modelBuilder.Entity<IndexerCatalogSourceEntry>();
        entry.ToTable("indexer_catalog_entries");
        entry.HasKey(x => new { x.SourceId, x.Key });
        entry.Property(x => x.Key).HasMaxLength(IndexerCatalogSourceEntry.KeyMaxLength);
        entry.Property(x => x.Name).HasMaxLength(Indexer.NameMaxLength);
        entry.Property(x => x.Description).HasMaxLength(IndexerCatalogSourceEntry.DescriptionMaxLength);
        entry.Property(x => x.ReleaseProtocol).HasConversion<string>().HasMaxLength(20);
        entry.Property(x => x.LimitsUnit).HasConversion<string>().HasMaxLength(10);
        entry.Property(x => x.RawDefinition).HasMaxLength(IndexerDefinitionParser.MaxRawContentLength);
        entry.HasOne<IndexerCatalogSource>()
            .WithMany()
            .HasForeignKey(x => x.SourceId)
            .OnDelete(DeleteBehavior.Cascade);

        // One row per indexer, deleted with the credential that produced it (SetCredentialAsync /
        // ClearCredentialAsync), so the foreign key only ever needs to keep the indexer honest.
        // The same cipher-without-nonce check the credential columns carry: a jar missing either
        // half can never be read back, so the database refuses the row outright.
        var session = modelBuilder.Entity<IndexerSession>();
        session.ToTable("indexer_sessions", t =>
        {
            // All three halves of a readable jar, not just two: a cipher without the key id that
            // encrypted it is exactly as unreadable as one without its nonce, so the database refuses
            // that row too rather than storing something only the entity keeps coherent.
            t.HasCheckConstraint(
                "ck_indexer_sessions_cookies_cipher_with_nonce",
                "(cookies_cipher IS NULL) = (cookies_nonce IS NULL) "
                + "AND (cookies_cipher IS NULL) = (cookies_key_id IS NULL)");
        });
        session.HasKey(x => x.IndexerId);
        session.Property(x => x.CookiesKeyId).HasMaxLength(32);
        // Cascade, not restrict: a session is a pure dependent — it authenticates one indexer and
        // means nothing without it. DeleteIndexerAsync removes the indexer; this cascade, and the
        // explicit session delete in that method, keep the row from surviving it.
        session.HasOne<Indexer>()
            .WithMany()
            .HasForeignKey(x => x.IndexerId)
            .OnDelete(DeleteBehavior.Cascade);

        var usage = modelBuilder.Entity<IndexerUsage>();
        usage.ToTable("indexer_usage");
        usage.HasKey(x => new { x.IndexerId, x.UsageDate });
        usage.HasOne<Indexer>().WithMany().HasForeignKey(x => x.IndexerId).OnDelete(DeleteBehavior.Cascade);

        // search_executions and search_results are declaratively partitioned by month (see the
        // PartitionSearchHistory migration and SearchPartitions). Two consequences are visible in the
        // model and must stay: the primary key contains the partition key, and there is no foreign
        // key between the two tables — PostgreSQL would require the child to carry the parent's
        // partition key as well, which would defeat co-partitioning them on their own timestamps.
        var execution = modelBuilder.Entity<SearchExecution>();
        execution.ToTable("search_executions");
        execution.HasKey(x => new { x.Id, x.StartedAt });
        execution.Property(x => x.Term).HasMaxLength(500);
        execution.Property(x => x.ContentKind).HasMaxLength(20);
        execution.Property(x => x.TvdbId).HasMaxLength(50);
        execution.Property(x => x.ImdbId).HasMaxLength(50);
        execution.HasIndex(x => x.StartedAt).HasDatabaseName("ix_search_executions_started_at");

        var result = modelBuilder.Entity<SearchResult>();
        result.ToTable("search_results");
        result.HasKey(x => new { x.Id, x.FoundAt });
        result.Property(x => x.ReleaseGuid).HasMaxLength(SearchResult.ReleaseGuidMaxLength);
        result.Property(x => x.Title).HasMaxLength(SearchResult.TitleMaxLength);
        result.Property(x => x.DownloadUrl).HasMaxLength(SearchResult.DownloadUrlMaxLength);
        result.Property(x => x.Protocol).HasConversion<string>().HasMaxLength(20);
        result.Property(x => x.IndexerName).HasMaxLength(200);
        result.Property(x => x.TvdbId).HasMaxLength(SearchResult.TvdbIdMaxLength);
        result.Property(x => x.Category).HasMaxLength(SearchResult.CategoryMaxLength);
        result.HasIndex(x => x.ExecutionId).HasDatabaseName("ix_search_results_execution_id");
    }
}
