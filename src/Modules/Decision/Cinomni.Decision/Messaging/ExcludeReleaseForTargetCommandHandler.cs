using Cinomni.Decision.Persistence;
using Cinomni.Kernel.Identifiers;
using Cinomni.Kernel.Messaging;
using Cinomni.Kernel.Results;
using Cinomni.Operations.Transactions;
using Microsoft.EntityFrameworkCore;

namespace Cinomni.Decision.Messaging;

/// <summary>Records that a release failed a goal, once however often it is told.</summary>
public sealed class ExcludeReleaseForTargetCommandHandler(DecisionDbContext dbContext, IUnitOfWork unitOfWork)
    : ICommandHandler<ExcludeReleaseForTargetCommand>
{
    private const int GuidMaxLength = 500;
    private const int ReasonMaxLength = 500;

    public async Task<Result> HandleAsync(ExcludeReleaseForTargetCommand command, CancellationToken cancellationToken = default)
    {
        var releaseGuid = Truncate(command.ReleaseGuid, GuidMaxLength);
        if (releaseGuid.Length == 0
            || await dbContext.ReleaseExclusions.AnyAsync(
                x => x.TargetId == command.TargetId && x.ReleaseGuid == releaseGuid, cancellationToken))
        {
            return Result.Success(); // nothing to name, or already excluded — a second attempt failing on it too
        }

        dbContext.ReleaseExclusions.Add(new TargetReleaseExclusionRecord
        {
            Id = Uuid7.New(),
            TargetId = command.TargetId,
            ReleaseGuid = releaseGuid,
            Reason = Truncate(command.Reason, ReasonMaxLength),
            CreatedAt = DateTimeOffset.UtcNow,
        });

        try
        {
            await unitOfWork.ExecuteAsync(dbContext.SaveChangesAsync, cancellationToken);
        }
        catch (DbUpdateException)
        {
            // A concurrent delivery may have recorded it first, and then the unique index settled the race.
            // Anything else is a real failure, and the command is retried.
            var recorded = await dbContext.ReleaseExclusions.AsNoTracking().AnyAsync(
                x => x.TargetId == command.TargetId && x.ReleaseGuid == releaseGuid, cancellationToken);
            if (!recorded)
            {
                throw;
            }
        }

        return Result.Success();
    }

    private static string Truncate(string value, int max) => value.Length > max ? value[..max] : value;
}
