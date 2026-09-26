using Cinomni.Catalog.Contracts;
using Cinomni.Kernel.Security;
using Cinomni.Notifications.Contracts;
using Cinomni.Notifications.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Cinomni.Notifications.Application;

/// <summary>
/// Read model and inbox actions over the in-app notifications: list newest-first (optionally unread
/// only), the unread badge count, and marking read. Three rules apply to every query, and all come from
/// the reader rather than from the request:
/// <list type="bullet">
/// <item>Audience — an operator notification never reaches a regular account, not even by id.</item>
/// <item>Content access — a notification about a work the reader may not see does not reach them
/// either. Its title is the work's title, so showing it would put a hidden or above-the-ceiling title
/// in a member's inbox, with its id beside it.</item>
/// <item>Read state is per account — one member marking something read leaves everyone else's badge
/// alone, because "read" is a receipt row, not a column on the notification.</item>
/// </list>
/// Marking read emits no event, so it is a plain write rather than a unit-of-work one.
/// </summary>
public sealed class NotificationInboxService(NotificationsDbContext dbContext, IContentAccess access)
    : INotificationQuery, INotificationInbox
{
    private const int MaxLimit = 200;

    public async Task<IReadOnlyList<Notification>> ListAsync(
        Viewer reader,
        bool unreadOnly,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var hidden = await HiddenWorkIdsAsync(reader, cancellationToken);

        // Left join the reader's receipts: one query answers both "what is there" and "have I read it".
        var query =
            from notification in Visible(reader, hidden).AsNoTracking()
            join receipt in dbContext.Reads.AsNoTracking().Where(r => r.UserId == reader.UserId)
                on notification.Id equals receipt.NotificationId into receipts
            from receipt in receipts.DefaultIfEmpty()
            where !unreadOnly || receipt == null
            orderby notification.CreatedAt descending
            select new { Notification = notification, Read = receipt != null };

        var rows = await query.Take(Math.Clamp(limit, 1, MaxLimit)).ToListAsync(cancellationToken);
        return rows.Select(row => row.Notification.ToContract(row.Read)).ToList();
    }

    public async Task<int> UnreadCountAsync(Viewer reader, CancellationToken cancellationToken = default)
    {
        var hidden = await HiddenWorkIdsAsync(reader, cancellationToken);
        return await Visible(reader, hidden).CountAsync(
            notification => !dbContext.Reads.Any(r => r.NotificationId == notification.Id && r.UserId == reader.UserId),
            cancellationToken);
    }

    public async Task MarkReadAsync(Viewer reader, Guid notificationId, CancellationToken cancellationToken = default)
    {
        // Visible() is what stops a member from acknowledging (and hiding) an operator notification, or
        // learning by the answer that one about a hidden title exists.
        var hidden = await HiddenWorkIdsAsync(reader, cancellationToken);
        var exists = await Visible(reader, hidden).AnyAsync(n => n.Id == notificationId, cancellationToken);
        if (!exists)
        {
            return;
        }

        dbContext.Reads.Add(NotificationReadRecord.Create(notificationId, reader.UserId, DateTimeOffset.UtcNow));
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsAlreadyRead(ex))
        {
            // Marking twice is the same as marking once — the receipt is already there.
            dbContext.ChangeTracker.Clear();
        }
    }

    public async Task MarkAllReadAsync(Viewer reader, CancellationToken cancellationToken = default)
    {
        // One statement: a receipt for every visible notification this reader has not acknowledged yet.
        // ON CONFLICT keeps it idempotent under a concurrent mark-read of the same notification.
        var adminOnly = reader.IsAdministrator;
        var hidden = (await HiddenWorkIdsAsync(reader, cancellationToken)).ToArray();
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO notifications.notification_reads (notification_id, user_id, read_at)
             SELECT n.id, {reader.UserId}, {DateTimeOffset.UtcNow}
             FROM notifications.notifications n
             WHERE ({adminOnly} OR NOT n.admin_only)
               AND (n.work_id IS NULL OR NOT (n.work_id = ANY({hidden})))
             ON CONFLICT (notification_id, user_id) DO NOTHING
             """,
            cancellationToken);
    }

    /// <summary>The notifications this reader is allowed to see — the one place audience and access are applied.</summary>
    private IQueryable<NotificationRecord> Visible(Viewer reader, IReadOnlyCollection<Guid> hiddenWorkIds)
    {
        if (reader.IsAdministrator)
        {
            return dbContext.Notifications;
        }

        var query = dbContext.Notifications.Where(n => !n.AdminOnly);
        return hiddenWorkIds.Count == 0
            ? query
            : query.Where(n => n.WorkId == null || !hiddenWorkIds.Contains(n.WorkId.Value));
    }

    /// <summary>
    /// The works named by this reader's notifications that they may not see. Catalog owns the answer and
    /// this module cannot join its schema, so it is asked once, about the distinct works the inbox
    /// mentions — a set bounded by the catalog, not by how many notifications there are.
    /// </summary>
    private async Task<IReadOnlyCollection<Guid>> HiddenWorkIdsAsync(Viewer reader, CancellationToken cancellationToken)
    {
        if (reader.IsAdministrator)
        {
            return [];
        }

        var mentioned = await dbContext.Notifications.AsNoTracking()
            .Where(n => !n.AdminOnly && n.WorkId != null)
            .Select(n => n.WorkId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);
        if (mentioned.Count == 0)
        {
            return [];
        }

        var visible = await access.FilterWorksAsync(reader, mentioned, cancellationToken);
        return mentioned.Where(id => !visible.Contains(id)).ToList();
    }

    private static bool IsAlreadyRead(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
