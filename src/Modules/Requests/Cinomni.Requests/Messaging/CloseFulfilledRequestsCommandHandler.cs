using Cinomni.Kernel.Messaging;
using Cinomni.Kernel.Results;
using Cinomni.Operations.Transactions;
using Cinomni.Requests.Contracts;
using Cinomni.Requests.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cinomni.Requests.Messaging;

/// <summary>
/// Closes every approved request that points at a work which just became available: Approved → Available.
/// Idempotent — a request already closed (or never approved) is skipped, so a redelivered event is a no-op.
/// </summary>
public sealed class CloseFulfilledRequestsCommandHandler(
    RequestsDbContext dbContext,
    IUnitOfWork unitOfWork) : ICommandHandler<CloseFulfilledRequestsCommand>
{
    public async Task<Result> HandleAsync(
        CloseFulfilledRequestsCommand command,
        CancellationToken cancellationToken = default)
    {
        var requests = await dbContext.MediaRequests
            .Where(r => r.WorkId == command.WorkId && r.Status == MediaRequestStatus.Approved)
            .ToListAsync(cancellationToken);

        var closed = requests.Count(request => request.MarkAvailable());
        if (closed == 0)
        {
            return Result.Success();
        }

        await unitOfWork.ExecuteAsync(async token => await dbContext.SaveChangesAsync(token), cancellationToken);
        return Result.Success();
    }
}
