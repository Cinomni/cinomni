using Cinomni.Decision.Application;
using Cinomni.Kernel.Messaging;
using Cinomni.Kernel.Results;

namespace Cinomni.Decision.Messaging;

/// <summary>Runs the cutoff assessment for what an import registered.</summary>
public sealed class AssessUpgradeCommandHandler(UpgradeAssessment assessment)
    : ICommandHandler<AssessUpgradeCommand>
{
    public async Task<Result> HandleAsync(AssessUpgradeCommand command, CancellationToken cancellationToken = default)
    {
        await assessment.AssessAsync(command.AssetId, command.WorkId, command.UnitIds, cancellationToken);
        return Result.Success();
    }
}
