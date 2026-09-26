using Cinomni.Discovery.Contracts;
using Cinomni.Kernel.Messaging;
using Cinomni.Kernel.Results;
using Cinomni.Operations.Messaging;

namespace Cinomni.Discovery.Messaging;

/// <summary>
/// Executes a federated search and announces <see cref="SearchCompleted"/>. The event is
/// best-effort (event-catalog): it is published after the results are committed, so a consumer
/// that sees it can always read the results by id; if it is lost, the next SearchRequested
/// re-drives the search.
/// </summary>
public sealed class ExecuteSearchCommandHandler(
    IReleaseSearch releaseSearch,
    IEventBus eventBus)
    : ICommandHandler<ExecuteSearchCommand>
{
    public async Task<Result> HandleAsync(
        ExecuteSearchCommand command,
        CancellationToken cancellationToken = default)
    {
        var origin = new SearchOrigin(command.TargetId, command.WorkId, command.UnitIds);
        var outcome = await releaseSearch.SearchAsync(command.Criterion, origin, cancellationToken);

        await eventBus.PublishAsync(
            new SearchCompleted(outcome.ExecutionId.Value, command.TargetId, outcome.Candidates.Count),
            cancellationToken);

        return Result.Success();
    }
}
