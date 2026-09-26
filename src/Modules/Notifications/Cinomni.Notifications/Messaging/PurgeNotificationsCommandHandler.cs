using Cinomni.Kernel.Messaging;
using Cinomni.Kernel.Results;
using Cinomni.Notifications.Application;
using Cinomni.Notifications.Persistence;
using Cinomni.Operations.Retention;
using Microsoft.Extensions.Logging;

namespace Cinomni.Notifications.Messaging;

/// <summary>
/// Ages notifications out of the inbox once they are older than the window, read or not. Read
/// receipts follow through the schema's cascade, so no account is left pointing at a notification
/// that no longer exists.
/// <para>
/// The configured delivery channels are never touched: they are configuration, not history.
/// </para>
/// </summary>
public sealed class PurgeNotificationsCommandHandler(
    NotificationsDbContext dbContext,
    NotificationRetentionOptions options,
    RetentionOptions platformOptions,
    ILogger<PurgeNotificationsCommandHandler> logger)
    : ICommandHandler<PurgeNotificationsCommand>
{
    public async Task<Result> HandleAsync(
        PurgeNotificationsCommand command,
        CancellationToken cancellationToken = default)
    {
        var cutoff = DateTimeOffset.UtcNow - options.NotificationRetention;

        var removed = await RetentionPurge.DeleteInBatchesAsync(
            dbContext.Notifications,
            notification => notification.CreatedAt < cutoff,
            notification => notification.Id,
            platformOptions.BatchSize,
            cancellationToken);

        logger.LogInformation(
            "Retention purge (notifications): removed {Removed} notifications with their read receipts.",
            removed);

        return Result.Success();
    }
}
