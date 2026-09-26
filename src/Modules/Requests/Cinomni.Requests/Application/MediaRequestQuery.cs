using Cinomni.Requests.Contracts;
using Cinomni.Requests.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cinomni.Requests.Application;

/// <summary>
/// Read model over the requests: the newest-first list (optionally narrowed to a status and/or a single
/// requester — how a non-admin only ever sees their own) and the pending count behind the approval badge.
/// </summary>
public sealed class MediaRequestQuery(RequestsDbContext dbContext) : IMediaRequestQuery
{
    private const int MaxLimit = 200;

    public async Task<IReadOnlyList<MediaRequest>> ListAsync(
        MediaRequestStatus? status,
        Guid? requestedByUserId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var query = dbContext.MediaRequests.AsNoTracking();

        if (status is not null)
        {
            query = query.Where(r => r.Status == status);
        }

        if (requestedByUserId is not null)
        {
            query = query.Where(r => r.RequestedByUserId == requestedByUserId);
        }

        var records = await query
            .OrderByDescending(r => r.RequestedAt)
            .Take(Math.Clamp(limit, 1, MaxLimit))
            .ToListAsync(cancellationToken);

        return records.Select(r => r.ToContract()).ToList();
    }

    public Task<int> PendingCountAsync(CancellationToken cancellationToken = default) =>
        dbContext.MediaRequests.CountAsync(r => r.Status == MediaRequestStatus.Pending, cancellationToken);
}
