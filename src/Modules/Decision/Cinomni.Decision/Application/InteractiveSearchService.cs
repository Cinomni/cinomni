using Cinomni.Catalog.Contracts;
using Cinomni.Decision.Contracts;
using Cinomni.Decision.Persistence;
using Cinomni.Discovery.Contracts;
using Cinomni.Kernel.Results;
using Cinomni.Monitoring.Contracts;
using Cinomni.Operations.Messaging;
using Cinomni.Operations.Transactions;
using Cinomni.ReleaseParsing.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Cinomni.Decision.Application;

/// <summary>
/// The manual half of the acquisition spine: search one target now, read why each release was accepted
/// or rejected, and take one of them regardless. Everything it shows comes from the same evaluator the
/// scheduled pipeline uses (<see cref="DecisionEngine.InspectSearchAsync"/>), so the explanation a
/// person reads is the explanation the automation acts on.
/// </summary>
public sealed class InteractiveSearchService(
    DecisionDbContext dbContext,
    IUnitOfWork unitOfWork,
    IEventBus eventBus,
    ITargetSearchPlans searchPlans,
    IReleaseSearch releaseSearch,
    IReleaseSearchResults searchResults,
    IReleaseParser parser,
    ICatalogSeriesQuery seriesQuery,
    DecisionEngine engine)
    : IInteractiveSearch
{
    /// <summary>The rule name under which a manual override is recorded on the evaluation it overrode.</summary>
    internal const string OverrideRule = "ManualOverride";

    private static readonly Error NotFoundError = new("decision.evaluation_not_found", "No such release evaluation.");

    private static readonly Error BlockedError = new(
        "decision.release_blocked",
        "This release is blocked. Unblock it before grabbing it; grabbing does not lift the block.");

    public async Task<Result<InteractiveSearchResult>> SearchAsync(
        Guid targetId,
        CancellationToken cancellationToken = default)
    {
        var plan = await searchPlans.ResolveAsync(new MonitoredTargetId(targetId), cancellationToken);
        if (plan is null)
        {
            return Result<InteractiveSearchResult>.Failure(new Error(
                "decision.target_not_searchable",
                "This target cannot be searched: it is unknown, its work is missing, or it is a series root."));
        }

        // The federated search runs outside any transaction (it is HTTP fan-out) and records its own
        // execution. It publishes no SearchCompleted, so nothing downstream reacts: this search exists
        // to be read, not to trigger the automatic pipeline a second time.
        var outcome = await releaseSearch.SearchAsync(
            plan.Criterion,
            new SearchOrigin(targetId, plan.WorkId.Value, plan.UnitIds),
            cancellationToken);

        var candidates = await engine.InspectSearchAsync(outcome.ExecutionId.Value, targetId, cancellationToken);

        return Result<InteractiveSearchResult>.Success(new InteractiveSearchResult(
            targetId,
            plan.WorkId.Value,
            outcome.ExecutionId.Value,
            plan.Criterion.Term,
            plan.Label,
            candidates));
    }

    public async Task<Result<ManualSelection>> GrabAsync(
        Guid evaluationId,
        CancellationToken cancellationToken = default)
    {
        var evaluation = await dbContext.ReleaseEvaluations
            .Include(e => e.Reasons)
            .FirstOrDefaultAsync(e => e.Id == evaluationId, cancellationToken);

        if (evaluation is null)
        {
            return Result<ManualSelection>.Failure(NotFoundError);
        }

        if (evaluation.TargetId is not { } targetId)
        {
            // Nothing to route to: an evaluation with no target came from a bare API search, and
            // Acquisition has no goal to feed it into.
            return Result<ManualSelection>.Failure(new Error(
                "decision.evaluation_not_routable",
                "This evaluation is not attached to a monitored target, so it cannot be acquired."));
        }

        if (await dbContext.ReleaseBlocks.AsNoTracking()
                .AnyAsync(b => b.ReleaseGuid == evaluation.ReleaseGuid, cancellationToken))
        {
            return Result<ManualSelection>.Failure(BlockedError);
        }

        var selection = new ManualSelection(
            new ReleaseEvaluationId(evaluation.Id), targetId, evaluation.ReleaseTitle, evaluation.Verdict,
            OverridesVerdict(evaluation));

        // Already granted: say so and publish nothing. The downstream command queue would deduplicate a
        // second selection anyway, but a repeat must not append a second override reason to an
        // append-only explanation.
        if (evaluation.Reasons.Any(r => r.Rule == OverrideRule))
        {
            return Result<ManualSelection>.Success(selection);
        }

        var candidate = await FindCandidateAsync(evaluation, cancellationToken);
        if (candidate is null)
        {
            // The execution's results are gone (purged, or the search predates the row). Without the
            // download link there is nothing to hand over, and inventing one is not an option.
            return Result<ManualSelection>.Failure(new Error(
                "decision.release_unavailable",
                "This release is no longer available from the search that found it. Search again."));
        }

        var unitIds = await ResolveUnitsAsync(evaluation, candidate, cancellationToken);
        var blockedMeanwhile = false;
        var purgedMeanwhile = false;

        await unitOfWork.ExecuteAsync(async token =>
        {
            // Re-read under the evaluation's lock: a block or a second grab landing at the same moment
            // would otherwise take the same line number, and the loser would answer 500.
            var trail = await EvaluationTrail.LockAsync(dbContext, evaluation.Id, token);
            if (trail is null)
            {
                purgedMeanwhile = true;
                return;
            }

            if (trail.Any(r => r.Rule == OverrideRule))
            {
                return;
            }

            if (await dbContext.ReleaseBlocks.AsNoTracking()
                    .AnyAsync(b => b.ReleaseGuid == evaluation.ReleaseGuid, token))
            {
                blockedMeanwhile = true;
                return;
            }

            EvaluationTrail.Append(
                dbContext, evaluation.Id, trail, OverrideRule, "verdict", evaluation.Verdict.ToString(),
                "selected by an administrator", ReasonOutcome.Pass, rejection: null);

            await dbContext.SaveChangesAsync(token);

            await eventBus.PublishAsync(
                new ReleaseSelected(
                    evaluation.Id, targetId, candidate.Guid, candidate.DownloadUrl,
                    UnitIds: unitIds.Count > 0 ? unitIds : null,
                    Release: ReleaseInfo.Of(candidate)),
                token);
        }, cancellationToken);

        if (purgedMeanwhile)
        {
            return Result<ManualSelection>.Failure(NotFoundError);
        }

        return blockedMeanwhile
            ? Result<ManualSelection>.Failure(BlockedError)
            : Result<ManualSelection>.Success(selection);
    }

    private static bool OverridesVerdict(ReleaseEvaluationRecord evaluation) =>
        EvaluationTrail.GrabOverridesVerdict(evaluation.Verdict, evaluation.Reasons.Select(r => (r.Rule, r.Outcome)));

    /// <summary>The raw release behind an evaluation, read back from the execution that found it.</summary>
    private async Task<ReleaseCandidate?> FindCandidateAsync(
        ReleaseEvaluationRecord evaluation,
        CancellationToken cancellationToken)
    {
        var results = await searchResults.GetResultsAsync(
            new SearchExecutionId(evaluation.SearchId), cancellationToken);

        return results.FirstOrDefault(r => string.Equals(r.Guid, evaluation.ReleaseGuid, StringComparison.Ordinal));
    }

    /// <summary>
    /// The catalog units this hand-picked release should satisfy: the ones it actually covers, and
    /// otherwise the ones the search was for. The fallback is what makes an override work at all — a
    /// release a person picks in spite of the verdict is often one whose numbering did not resolve, and
    /// handing Acquisition no units would leave it routing by target with no idea what it just closed.
    /// </summary>
    private async Task<IReadOnlyList<Guid>> ResolveUnitsAsync(
        ReleaseEvaluationRecord evaluation,
        ReleaseCandidate candidate,
        CancellationToken cancellationToken)
    {
        var context = await searchResults.GetRequestContextAsync(
            new SearchExecutionId(evaluation.SearchId), cancellationToken);

        var requested = context?.RequestedUnitIds ?? [];
        var parsed = parser.Parse(candidate.Title);
        if (parsed.IsFailure)
        {
            return requested;
        }

        var covered = await new EpisodeCoverageResolver(seriesQuery)
            .ResolveAsync(context?.WorkId, parsed.Value.Numbering, requested, cancellationToken);

        return covered.Count > 0 ? covered : requested;
    }
}
