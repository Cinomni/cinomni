using Cinomni.Kernel.Results;
using Cinomni.Kernel.Security;

namespace Cinomni.Notifications.Contracts;

/// <summary>
/// Read model over the in-app notification inbox. Every call is answered for one reader: they see the
/// notifications meant for their audience, and "read" means <em>they</em> read it — one account marking a
/// notification read never clears it for anyone else.
/// </summary>
public interface INotificationQuery
{
    Task<IReadOnlyList<Notification>> ListAsync(
        Viewer reader,
        bool unreadOnly,
        int limit,
        CancellationToken cancellationToken = default);

    Task<int> UnreadCountAsync(Viewer reader, CancellationToken cancellationToken = default);
}

/// <summary>
/// Inbox actions: mark notifications read for one account. A notification the reader is not allowed to
/// see cannot be marked read, not even by id.
/// </summary>
public interface INotificationInbox
{
    Task MarkReadAsync(Viewer reader, Guid notificationId, CancellationToken cancellationToken = default);

    Task MarkAllReadAsync(Viewer reader, CancellationToken cancellationToken = default);
}

/// <summary>Manages the outbound delivery channels.</summary>
public interface INotificationChannels
{
    Task<IReadOnlyList<NotificationChannel>> ListAsync(CancellationToken cancellationToken = default);

    Task<Result<Guid>> AddAsync(NotificationChannelKind kind, string name, string target, CancellationToken cancellationToken = default);

    Task SetEnabledAsync(Guid id, bool enabled, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
