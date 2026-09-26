using Microsoft.EntityFrameworkCore;

namespace Cinomni.Notifications.Persistence;

/// <summary>
/// EF Core context for the Notifications module. Owns the <c>notifications</c> schema: the
/// in-app notifications and the configured outbound delivery channels.
/// </summary>
public sealed class NotificationsDbContext(DbContextOptions<NotificationsDbContext> options)
    : DbContext(options)
{
    public const string SchemaName = "notifications";

    public DbSet<NotificationRecord> Notifications => Set<NotificationRecord>();

    public DbSet<NotificationChannelRecord> Channels => Set<NotificationChannelRecord>();

    public DbSet<NotificationReadRecord> Reads => Set<NotificationReadRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(SchemaName);

        var notification = modelBuilder.Entity<NotificationRecord>();
        notification.ToTable("notifications");
        notification.HasKey(x => x.Id);
        notification.Property(x => x.DedupKey).HasMaxLength(300);
        notification.Property(x => x.Type).HasMaxLength(100);
        // Enums stored as text — stable and readable, no magic integers.
        notification.Property(x => x.Severity).HasConversion<string>().HasMaxLength(20);
        notification.Property(x => x.Title).HasMaxLength(300);
        notification.Property(x => x.Body).HasMaxLength(2000);
        // One notification per source event — the idempotency backstop under retry/recovery.
        notification.HasIndex(x => x.DedupKey).IsUnique().HasDatabaseName("ux_notifications_dedup_key");
        // The inbox is browsed newest-first within an audience; unread is a left join, not a column.
        notification.HasIndex(x => new { x.AdminOnly, x.CreatedAt })
            .HasDatabaseName("ix_notifications_audience_created");
        // The audience index above leads on admin_only, so it cannot serve a bare age predicate.
        notification.HasIndex(x => x.CreatedAt).HasDatabaseName("ix_notifications_created_at");
        // A member's inbox first asks which works its notifications mention, to leave out the ones they
        // may not see; only household notifications about a work take part.
        notification.HasIndex(x => x.WorkId)
            .HasDatabaseName("ix_notifications_household_work")
            .HasFilter("work_id IS NOT NULL AND NOT admin_only");

        var read = modelBuilder.Entity<NotificationReadRecord>();
        read.ToTable("notification_reads");
        // One receipt per (notification, account): the key is the idempotency of "mark read".
        read.HasKey(x => new { x.NotificationId, x.UserId });
        // Reading the inbox joins receipts for one account; deleting a notification takes them with it.
        read.HasIndex(x => x.UserId).HasDatabaseName("ix_notification_reads_user");
        read.HasOne<NotificationRecord>()
            .WithMany()
            .HasForeignKey(x => x.NotificationId)
            .OnDelete(DeleteBehavior.Cascade);

        var channel = modelBuilder.Entity<NotificationChannelRecord>();
        channel.ToTable("notification_channels");
        channel.HasKey(x => x.Id);
        channel.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
        channel.Property(x => x.Name).HasMaxLength(200);
        channel.Property(x => x.Target).HasMaxLength(2000);
    }
}
