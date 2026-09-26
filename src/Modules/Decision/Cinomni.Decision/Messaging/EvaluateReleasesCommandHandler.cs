using Cinomni.Decision.Application;
using Cinomni.Kernel.Messaging;
using Cinomni.Kernel.Results;

namespace Cinomni.Decision.Messaging;

/// <summary>Runs the decision engine for a completed search.</summary>
public sealed class EvaluateReleasesCommandHandler(DecisionEngine engine)
    : ICommandHandler<EvaluateReleasesCommand>
{
    public async Task<Result> HandleAsync(EvaluateReleasesCommand command, CancellationToken cancellationToken = default)
    {
        await engine.EvaluateSearchAsync(command.SearchId, command.TargetId, cancellationToken);
        return Result.Success();
    }
}
