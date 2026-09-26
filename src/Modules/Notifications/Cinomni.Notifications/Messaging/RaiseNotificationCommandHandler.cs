using System.Data.Common;
using Cinomni.Catalog.Contracts;
using Cinomni.Kernel.Messaging;
using Cinomni.Kernel.Results;
using Cinomni.Notifications.Application;
using Cinomni.Notifications.Contracts;
using Cinomni.Notifications.Delivery;
using Cinomni.Notifications.Persistence;
using Cinomni.Operations.Messaging;
using Cinomni.Operations.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cinomni.Notifications.Messaging;

/// <summary>
/// Raises a notification: resolves the work title from Catalog (→i, best-effort), composes the message,
/// persists it (its own unit of work — the enqueuing event handler ran in the outbox transaction), then
/// fans it out to the enabled channels. Idempotent by the command's <c>DedupKey</c> (a stable business
/// key): a re-executed or recovered command is a no-op, never a duplicate notification or re-fired
/// webhook. Title resolution and delivery are best-effort — a Catalog or channel failure never stops the
/// in-app notification from landing.
/// </summary>
public sealed class RaiseNotificationCommandHandler(
    NotificationsDbContext dbContext,
    IUnitOfWork unitOfWork,
    IEventBus eventBus,
    ICatalogQuery catalog,
    INotificationDispatcher dispatcher,
    ILogger<RaiseNotificationCommandHandler> logger) : ICommandHandler<RaiseNotificationCommand>
{
    public async Task<Result> HandleAsync(RaiseNotificationCommand command, CancellationToken cancellationToken = default)
    {
        // Idempotent: a redelivered or recovered command for the same source event is a no-op. The unique
        // index on dedup_key is the DB-level backstop (the queue serialises a command, so this check wins).
        if (await dbContext.Notifications.AnyAsync(n => n.DedupKey == command.DedupKey, cancellationToken))
        {
            return Result.Success();
        }

        var workTitle = await ResolveTitleAsync(command.WorkId, cancellationToken);
        var composed = NotificationComposer.Compose(command.Kind, workTitle, command.Detail);

        var record = NotificationRecord.Create(
            command.DedupKey, composed.Type, composed.Severity, composed.Title, composed.Body, command.WorkId,
            composed.AdminOnly, DateTimeOffset.UtcNow);

        await unitOfWork.ExecuteAsync(async token =>
        {
            dbContext.Notifications.Add(record);
            await dbContext.SaveChangesAsync(token);

            // The inbox changed: announced in the same transaction as the row, so a live client is never
            // told about a notification that then failed to commit.
            await eventBus.PublishAsync(
                new NotificationRaised(
                    record.Id, record.Type, record.Severity.ToString(), record.AdminOnly, record.WorkId),
                token);
        }, cancellationToken);

        await FanOutAsync(record, cancellationToken);
        return Result.Success();
    }

    private async Task<string?> ResolveTitleAsync(Guid? workId, CancellationToken cancellationToken)
    {
        if (workId is null)
        {
            return null;
        }

        try
        {
            var work = await catalog.GetByIdAsync(new WorkId(workId.Value), cancellationToken);
            return work?.Title;
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException)
        {
            // Enrichment is best-effort — the notification still lands with the generic title.
            logger.LogWarning(ex, "Could not resolve work title {WorkId} for a notification.", workId);
            return null;
        }
    }

    private async Task FanOutAsync(NotificationRecord record, CancellationToken cancellationToken)
    {
        // Delivery is best-effort and must never fault the command (the notification is already persisted),
        // so both the channel read and each dispatch are guarded.
        List<NotificationChannelRecord> channels;
        try
        {
            channels = await dbContext.Channels.AsNoTracking().Where(c => c.Enabled).ToListAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Could not load notification channels for fan-out.");
            return;
        }

        if (channels.Count == 0)
        {
            return;
        }

        // Freshly raised, and an outbound channel has no reader anyway: unread is the only sensible value.
        var notification = record.ToContract(read: false);
        foreach (var channel in channels)
        {
            try
            {
                await dispatcher.DispatchAsync(channel.ToContract(), notification, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                logger.LogWarning(ex, "Notification delivery to channel {Channel} failed.", channel.Name);
            }
        }
    }
}
