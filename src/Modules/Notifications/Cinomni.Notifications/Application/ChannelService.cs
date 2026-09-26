using Cinomni.Kernel.Results;
using Cinomni.Notifications.Contracts;
using Cinomni.Notifications.Persistence;
using Cinomni.Operations.Transactions;
using Microsoft.EntityFrameworkCore;

namespace Cinomni.Notifications.Application;

/// <summary>
/// Manages the outbound delivery channels. Validates the target is an absolute http(s) URL at add time;
/// the real protection against internal targets is the SSRF-hardened client used at dispatch (anti
/// DNS-rebinding). Writes go through the unit of work for consistency with the rest of the module.
/// </summary>
public sealed class ChannelService(NotificationsDbContext dbContext, IUnitOfWork unitOfWork) : INotificationChannels
{
    public async Task<IReadOnlyList<NotificationChannel>> ListAsync(CancellationToken cancellationToken = default)
    {
        var records = await dbContext.Channels
            .AsNoTracking()
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(cancellationToken);

        return records.Select(c => c.ToContract()).ToList();
    }

    public async Task<Result<Guid>> AddAsync(
        NotificationChannelKind kind,
        string name,
        string target,
        CancellationToken cancellationToken = default)
    {
        var trimmedName = name.Trim();
        if (trimmedName.Length == 0)
        {
            return Result<Guid>.Failure(new Error("notifications.invalid_channel", "A channel name is required."));
        }

        if (!Uri.TryCreate(target.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return Result<Guid>.Failure(new Error("notifications.invalid_target", "The target must be an absolute http(s) URL."));
        }

        var channel = NotificationChannelRecord.Create(kind, trimmedName, uri.ToString(), DateTimeOffset.UtcNow);
        await unitOfWork.ExecuteAsync(async token =>
        {
            dbContext.Channels.Add(channel);
            await dbContext.SaveChangesAsync(token);
        }, cancellationToken);

        return Result<Guid>.Success(channel.Id);
    }

    public async Task SetEnabledAsync(Guid id, bool enabled, CancellationToken cancellationToken = default)
    {
        var channel = await dbContext.Channels.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (channel is null || channel.Enabled == enabled)
        {
            return;
        }

        channel.Enabled = enabled;
        await unitOfWork.ExecuteAsync(async token => await dbContext.SaveChangesAsync(token), cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var channel = await dbContext.Channels.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (channel is null)
        {
            return;
        }

        dbContext.Channels.Remove(channel);
        await unitOfWork.ExecuteAsync(async token => await dbContext.SaveChangesAsync(token), cancellationToken);
    }
}
