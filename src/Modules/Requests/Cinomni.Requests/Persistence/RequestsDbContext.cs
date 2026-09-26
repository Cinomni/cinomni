using Cinomni.Requests.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Cinomni.Requests.Persistence;

/// <summary>
/// EF Core context for the Requests module. Owns the <c>requests</c> schema: the user requests
/// and their decisions.
/// </summary>
public sealed class RequestsDbContext(DbContextOptions<RequestsDbContext> options) : DbContext(options)
{
    public const string SchemaName = "requests";

    public DbSet<MediaRequestRecord> MediaRequests => Set<MediaRequestRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(SchemaName);

        var request = modelBuilder.Entity<MediaRequestRecord>();
        request.ToTable("media_requests");
        request.HasKey(x => x.Id);
        request.Property(x => x.Title).HasMaxLength(500);
        request.Property(x => x.Provider).HasMaxLength(50);
        request.Property(x => x.ExternalId).HasMaxLength(100);
        request.Property(x => x.RequestedByUsername).HasMaxLength(100);
        request.Property(x => x.DecisionNote).HasMaxLength(500);
        // Enums stored as text — stable and readable, no magic integers.
        request.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        request.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20).HasDefaultValue(MediaRequestKind.Movie);

        // At most one live request per title: a rejected one may be submitted again, so the uniqueness is
        // partial. This is the DB-level backstop behind the duplicate check in the service. The kind is
        // part of the title's identity: a provider can number films and shows independently.
        request.HasIndex(x => new { x.Provider, x.ExternalId, x.Kind })
            .IsUnique()
            .HasFilter($"status <> '{nameof(MediaRequestStatus.Rejected)}'")
            .HasDatabaseName("ux_media_requests_active_provider_external_id_kind");

        // The inbox is browsed newest-first and filtered by status or by requester.
        request.HasIndex(x => new { x.Status, x.RequestedAt }).HasDatabaseName("ix_media_requests_status_requested");
        request.HasIndex(x => x.RequestedByUserId).HasDatabaseName("ix_media_requests_requested_by");
        // Closing a request when its work becomes available looks it up by work.
        request.HasIndex(x => x.WorkId).HasDatabaseName("ix_media_requests_work");
    }
}
