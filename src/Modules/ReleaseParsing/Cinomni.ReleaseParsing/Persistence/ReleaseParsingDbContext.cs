using Microsoft.EntityFrameworkCore;

namespace Cinomni.ReleaseParsing.Persistence;

/// <summary>
/// EF Core context for the Release Parsing module. Owns the <c>parsing</c> schema:
/// the parse audit trail and the parser rule-set versions.
/// </summary>
public sealed class ReleaseParsingDbContext(DbContextOptions<ReleaseParsingDbContext> options)
    : DbContext(options)
{
    public const string SchemaName = "parsing";

    public DbSet<ParsedReleaseRecord> ParsedReleases => Set<ParsedReleaseRecord>();

    public DbSet<ParseRuleVersion> ParseRuleVersions => Set<ParseRuleVersion>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(SchemaName);

        var parsed = modelBuilder.Entity<ParsedReleaseRecord>();
        parsed.ToTable("parsed_releases");
        parsed.HasKey(x => x.Id);
        parsed.Property(x => x.SourceTitle).HasMaxLength(1000);
        parsed.Property(x => x.ReleaseType).HasConversion<string>().HasMaxLength(20);
        parsed.Property(x => x.QualityJson).HasColumnType("jsonb");
        parsed.Property(x => x.RevisionJson).HasColumnType("jsonb");
        parsed.Property(x => x.LanguagesJson).HasColumnType("jsonb");
        parsed.Property(x => x.NumberingJson).HasColumnType("jsonb");
        parsed.Property(x => x.ReleaseGroup).HasMaxLength(ParsedReleaseRecords.ReleaseGroupMaxLength);
        parsed.Property(x => x.Edition).HasMaxLength(ParsedReleaseRecords.EditionMaxLength);
        parsed.Property(x => x.CanonicalKey).HasMaxLength(ParsedReleaseRecords.CanonicalKeyMaxLength);
        parsed.Property(x => x.InfoHash).HasMaxLength(ParsedReleaseRecords.InfoHashMaxLength);
        parsed.Property(x => x.ParserVersion).HasMaxLength(20);
        parsed.HasIndex(x => x.CanonicalKey).HasDatabaseName("ix_parsed_releases_canonical_key");
        parsed.HasIndex(x => new { x.Season, x.EpisodeFrom })
            .HasDatabaseName("ix_parsed_releases_season_episode");
        // Drives the retention sweep, which walks the audit trail by age.
        parsed.HasIndex(x => x.CreatedAt).HasDatabaseName("ix_parsed_releases_created_at");

        var ruleVersion = modelBuilder.Entity<ParseRuleVersion>();
        ruleVersion.ToTable("parse_rule_versions");
        ruleVersion.HasKey(x => x.Version);
        ruleVersion.Property(x => x.Version).HasMaxLength(20);
        ruleVersion.Property(x => x.Notes).HasMaxLength(500);
    }
}
