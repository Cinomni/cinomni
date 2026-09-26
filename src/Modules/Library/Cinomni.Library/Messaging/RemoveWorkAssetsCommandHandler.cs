using Cinomni.Kernel.Messaging;
using Cinomni.Kernel.Results;
using Cinomni.Library.Persistence;
using Cinomni.Operations.Transactions;
using Microsoft.EntityFrameworkCore;

namespace Cinomni.Library.Messaging;

/// <summary>
/// Marks every live asset of a removed work <c>Removed</c>, so nothing offers it for playback again. The
/// rows stay: they are the record of what the library held, and what Import reads to find the files when
/// the administrator asked for those to go too. Idempotent: a redelivery finds them already removed.
/// </summary>
public sealed class RemoveWorkAssetsCommandHandler(LibraryDbContext dbContext, IUnitOfWork unitOfWork)
    : ICommandHandler<RemoveWorkAssetsCommand>
{
    public async Task<Result> HandleAsync(RemoveWorkAssetsCommand command, CancellationToken cancellationToken = default)
    {
        var assets = await dbContext.Assets
            .Include(a => a.Versions)
            .Where(a => a.WorkId == command.WorkId)
            .ToListAsync(cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var changed = assets.Count(asset => asset.MarkRemoved(now));
        if (changed > 0)
        {
            await unitOfWork.ExecuteAsync(dbContext.SaveChangesAsync, cancellationToken);
        }

        return Result.Success();
    }
}
