using Cinomni.Catalog.Contracts;
using Cinomni.Decision.Contracts;
using Cinomni.Decision.Evaluation;
using Cinomni.Decision.Persistence;
using Cinomni.Discovery.Contracts;
using Cinomni.ReleaseParsing.Contracts;
using Cinomni.Operations.Messaging;
using Cinomni.Operations.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cinomni.Decision.Application;

/// <summary>
/// Orchestrates a decision for one completed search: pulls the raw candidates from Discovery,
/// parses each, checks it is actually the thing that was asked for, evaluates it against the profile
/// for that content kind, persists the explainable evaluations, and announces one
/// <c>ReleaseEvaluated</c> per candidate plus a <c>ReleaseSelected</c> for the best acceptable one
/// (or <c>NoAcceptableRelease</c>). Idempotent per search.
/// <para>
/// The same engine answers an <em>interactive</em> search through <see cref="InspectSearchAsync"/>:
/// identical evaluation and identical persisted reasons, but no selection is announced, because there
/// the choice belongs to the person reading them. Sharing the path is the point — a second evaluator
/// would eventually explain a verdict the automatic pipeline no longer reaches.
/// </para>
/// </summary>
public sealed class DecisionEngine(
    DecisionDbContext dbContext,
    IUnitOfWork unitOfWork,
    IEventBus eventBus,
    IReleaseSearchResults searchResults,
    IReleaseParser parser,
    ICatalogSeriesQuery seriesQuery,
    CurrentQualityResolver currentQuality,
    ILogger<DecisionEngine> logger)
{
    /// <summary>The reason a release is passed over for the goal it already failed.</summary>
    internal const string FailedForTargetRule = "FailedForTarget";

    private readonly ReleaseEvaluator _evaluator = new();

    public async Task EvaluateSearchAsync(Guid searchId, Guid? targetId, CancellationToken cancellationToken = default)
    {
        // Idempotency: once a search has any persisted evaluation, a crash-retry re-emits nothing.
        // (A search with zero candidates persists no rows; the SearchCompleted reaction already
        // skips those, so this path only sees searches that had candidates.)
        if (await dbContext.ReleaseEvaluations.AnyAsync(e => e.SearchId == searchId, cancellationToken))
        {
            return;
        }

        var run = await EvaluateAsync(searchId, targetId, cancellationToken);
        if (run is null)
        {
            return;
        }

        await unitOfWork.ExecuteAsync(async token =>
        {
            await PersistAndAnnounceAsync(run, targetId, token);

            if (run.Winner is { } winner)
            {
                await eventBus.PublishAsync(
                    new ReleaseSelected(
                        winner.Record.Id,
                        targetId,
                        winner.Candidate.Guid,
                        winner.Candidate.DownloadUrl,
                        UnitIds: UnitsOf(winner),
                        Release: ReleaseInfo.Of(winner.Candidate)),
                    token);
            }
            else
            {
                await eventBus.PublishAsync(
                    new NoAcceptableRelease(searchId, targetId, run.Evaluations.Count), token);
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Evaluates a search for a human to choose from: the reasons are persisted and announced exactly as
    /// the automatic path does, so the explainability trail is the same trail, but nothing is selected.
    /// The candidate the engine <em>would</em> have taken is flagged rather than acted on.
    /// </summary>
    /// <returns>The evaluated candidates, best first; empty when no profile could judge them.</returns>
    public async Task<IReadOnlyList<EvaluatedCandidate>> InspectSearchAsync(
        Guid searchId,
        Guid? targetId,
        CancellationToken cancellationToken = default)
    {
        var run = await EvaluateAsync(searchId, targetId, cancellationToken);
        if (run is null)
        {
            return [];
        }

        await unitOfWork.ExecuteAsync(
            token => PersistAndAnnounceAsync(run, targetId, token), cancellationToken);

        var winnerId = run.Winner?.Record.Id;
        return Rank(run)
            .Select(evaluation => ToCandidate(evaluation, isRecommended: evaluation.Record.Id == winnerId))
            .ToList();
    }

    /// <summary>
    /// The shared core: parse, match, evaluate and rank, touching no transaction. Null when the search is
    /// unknown to the profile set — there is nothing to persist or announce in that case.
    /// </summary>
    private async Task<DecisionRun?> EvaluateAsync(Guid searchId, Guid? targetId, CancellationToken cancellationToken)
    {
        var executionId = new SearchExecutionId(searchId);
        var context = await searchResults.GetRequestContextAsync(executionId, cancellationToken);

        var profile = await LoadProfileAsync(ProfileScope.ForContentKind(context?.ContentKind), cancellationToken);
        if (profile is null)
        {
            logger.LogWarning("Search {SearchId} cannot be decided: no acquisition profile is configured.", searchId);
            return null;
        }

        var candidates = await searchResults.GetResultsAsync(executionId, cancellationToken);
        var request = BaseRequestOf(context);
        var coverage = new EpisodeCoverageResolver(seriesQuery);

        var requestedUnitIds = context?.RequestedUnitIds ?? [];

        // What we already hold for these units, once for the whole search: it is the same baseline for
        // every candidate, and asking per candidate would be one library round trip per indexer result.
        var current = await currentQuality.ResolveAsync(profile, requestedUnitIds, cancellationToken);
        var blocks = await LoadBlocksAsync(cancellationToken);
        var exclusions = await LoadExclusionsAsync(targetId, cancellationToken);

        var evaluations = new List<Evaluated>(candidates.Count);
        foreach (var candidate in candidates)
        {
            evaluations.Add(await EvaluateCandidateAsync(
                candidate, profile, searchId, targetId, request, context?.WorkId, requestedUnitIds, coverage,
                current, blocks, exclusions, cancellationToken));
        }

        return new DecisionRun(evaluations, SelectWinner(evaluations, request), request);
    }

    /// <summary>
    /// Writes the evaluations and announces one <c>ReleaseEvaluated</c> each. Runs inside the caller's
    /// unit of work so the rows and their events commit together.
    /// </summary>
    private async Task PersistAndAnnounceAsync(DecisionRun run, Guid? targetId, CancellationToken cancellationToken)
    {
        foreach (var evaluation in run.Evaluations)
        {
            dbContext.ReleaseEvaluations.Add(evaluation.Record);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        foreach (var evaluation in run.Evaluations)
        {
            await eventBus.PublishAsync(
                new ReleaseEvaluated(
                    evaluation.Record.Id,
                    targetId,
                    evaluation.Record.ReleaseGuid,
                    evaluation.Record.Verdict.ToString(),
                    evaluation.Record.CustomFormatScore,
                    ReleaseEvaluator.Version),
                cancellationToken);
        }
    }

    /// <summary>
    /// Ranks the acceptable candidates. For a season-level goal, coverage comes first: a pack that
    /// closes eight missing episodes beats a single episode of marginally better quality, because the
    /// alternative is eight separate searches, downloads and imports.
    /// </summary>
    private static Evaluated? SelectWinner(IReadOnlyList<Evaluated> evaluations, EvaluationRequest? request) =>
        Order(evaluations.Where(e => e.Outcome.Verdict == Verdict.Accepted && e.Block is null && e.Exclusion is null), request)
            .FirstOrDefault();

    /// <summary>
    /// Every candidate in the order a person should read them: the acceptable ones first, in the engine's
    /// own preference order, then the rejections. A rejected release is still worth showing — overriding
    /// one is the whole point of an interactive search — but it is never the first thing offered.
    /// </summary>
    private static IEnumerable<Evaluated> Rank(DecisionRun run) =>
        Order(run.Evaluations.Where(e => e.Outcome.Verdict == Verdict.Accepted), run.Request)
            .Concat(Order(run.Evaluations.Where(e => e.Outcome.Verdict != Verdict.Accepted), run.Request));

    private static IOrderedEnumerable<Evaluated> Order(IEnumerable<Evaluated> evaluations, EvaluationRequest? request)
    {
        var ranked = request?.PrefersCoverage == true
            ? evaluations.OrderByDescending(e => e.Outcome.EpisodeCoverage).ThenByDescending(e => e.Outcome.Rank)
            : evaluations.OrderByDescending(e => e.Outcome.Rank);

        return ranked
            .ThenByDescending(e => e.Outcome.CustomFormatScore)
            .ThenByDescending(e => e.Candidate.Seeders ?? 0)
            .ThenBy(e => e.Candidate.Guid, StringComparer.Ordinal);
    }

    private static EvaluatedCandidate ToCandidate(Evaluated evaluation, bool isRecommended) => new(
        new ReleaseEvaluationId(evaluation.Record.Id),
        evaluation.Record.ReleaseGuid,
        evaluation.Record.ReleaseTitle,
        evaluation.Candidate.IndexerName,
        evaluation.Candidate.Protocol.ToString(),
        evaluation.Candidate.SizeBytes,
        evaluation.Candidate.Seeders,
        evaluation.Candidate.PublishedAt,
        evaluation.Outcome.Verdict,
        evaluation.Outcome.CustomFormatScore,
        evaluation.Outcome.EpisodeCoverage,
        isRecommended,
        evaluation.Outcome.Reasons,
        evaluation.Block is not null,
        evaluation.Block?.Id,
        evaluation.Block?.Reason,
        EvaluationTrail.GrabOverridesVerdict(
            evaluation.Outcome.Verdict, evaluation.Outcome.Reasons.Select(r => (r.Rule, r.Outcome))),
        evaluation.Candidate.Leechers);

    /// <summary>
    /// The units the selection satisfies. Null when nothing was resolved, which means "route by
    /// target" — exactly the movie behaviour that ships today.
    /// </summary>
    private static IReadOnlyList<Guid>? UnitsOf(Evaluated winner) =>
        winner.Outcome.Covered.Count > 0 ? winner.Outcome.Covered : null;

    private static EvaluationRequest? BaseRequestOf(SearchRequestContext? context) =>
        context is null
            ? null
            : new EvaluationRequest(
                context.ContentKind,
                context.Term,
                context.SeasonNumber,
                context.EpisodeNumber,
                context.AbsoluteNumber,
                context.AirDate,
                // The work's year: what tells a series apart from its own reboot when the two share
                // a title exactly ("Doctor Who" 1963 and 2005).
                Year: context.Year);

    private async Task<Evaluated> EvaluateCandidateAsync(
        ReleaseCandidate candidate,
        AcquisitionProfile profile,
        Guid searchId,
        Guid? targetId,
        EvaluationRequest? request,
        Guid? workId,
        IReadOnlyList<Guid> requestedUnitIds,
        EpisodeCoverageResolver coverage,
        CurrentRelease? current,
        IReadOnlyDictionary<string, BlockedRelease> blocks,
        IReadOnlyDictionary<string, string> exclusions,
        CancellationToken cancellationToken)
    {
        var id = DeterministicId.From(searchId, candidate.Guid);
        var parseResult = parser.Parse(candidate.Title);
        var block = MatchBlock(blocks, candidate.Guid);
        var exclusion = MatchExclusion(exclusions, candidate.Guid);

        if (parseResult.IsFailure)
        {
            var reasons = new List<EvaluationReason>
            {
                new("Parseable", "title", "parseable", candidate.Title, ReasonOutcome.Fail, RejectionKind.Permanent),
            };
            if (block is not null)
            {
                reasons.Add(BlockReason(block));
            }

            var rejected = BuildRecord(id, profile.Id, searchId, targetId, candidate, canonicalKey: null,
                Verdict.RejectedPermanent, customFormatScore: 0, reasons);
            return new Evaluated(
                candidate, rejected, new EvaluationOutcome(Verdict.RejectedPermanent, 0, reasons, int.MinValue), block,
                exclusion);
        }

        var parsed = parseResult.Value;

        var candidateRequest = request;
        if (request is not null)
        {
            var covered = await coverage.ResolveAsync(workId, parsed.Numbering, requestedUnitIds, cancellationToken);
            candidateRequest = request with { CoveredUnitIds = covered };
        }

        var outcome = _evaluator.Evaluate(parsed, candidate, profile, candidateRequest, current);
        if (block is not null)
        {
            outcome = outcome with
            {
                Reasons = [.. outcome.Reasons, BlockReason(block)],
            };
        }

        if (exclusion is not null)
        {
            outcome = outcome with
            {
                Reasons = [.. outcome.Reasons, ExclusionReason(exclusion)],
            };
        }

        // Both lines are permanent failures, so the verdict says what they say. Stored as Accepted, a
        // release the sweep could never take counted as one it may have acted on: retention kept it
        // forever, and the interactive list offered it among the acceptable ones.
        if (block is not null || exclusion is not null)
        {
            outcome = outcome with { Verdict = Verdict.RejectedPermanent };
        }

        var record = BuildRecord(id, profile.Id, searchId, targetId, candidate, parsed.Identity.CanonicalKey,
            outcome.Verdict, outcome.CustomFormatScore, outcome.Reasons);
        return new Evaluated(candidate, record, outcome, block, exclusion);
    }

    private static EvaluationReason BlockReason(BlockedRelease block) => new(
        ReleaseBlocklist.BlockedRule,
        "releaseGuid",
        "not blocked",
        Truncate(block.Reason, 200),
        ReasonOutcome.Fail,
        RejectionKind.Permanent);

    private static ReleaseEvaluationRecord BuildRecord(
        Guid id,
        Guid profileId,
        Guid searchId,
        Guid? targetId,
        ReleaseCandidate candidate,
        string? canonicalKey,
        Verdict verdict,
        int customFormatScore,
        IReadOnlyList<EvaluationReason> reasons)
    {
        var record = new ReleaseEvaluationRecord
        {
            Id = id,
            ProfileId = profileId,
            SearchId = searchId,
            TargetId = targetId,
            ReleaseGuid = Truncate(candidate.Guid, 500)!,
            // Titles come from indexers (hostile, up to the column limit); a reason may carry the
            // raw title as its actual value. Truncate to the column width so one long, unparseable
            // title cannot overflow and roll back the whole batch of evaluations.
            ReleaseTitle = Truncate(candidate.Title, 1000)!,
            CanonicalKey = Truncate(canonicalKey, 500),
            Verdict = verdict,
            CustomFormatScore = customFormatScore,
            EvaluatorVersion = ReleaseEvaluator.Version,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        var seq = 0;
        foreach (var reason in reasons)
        {
            record.Reasons.Add(new DecisionReasonRecord
            {
                EvaluationId = id,
                Seq = seq++,
                Rule = Truncate(reason.Rule, 100)!,
                Property = Truncate(reason.Property, 50),
                ProfileValue = Truncate(reason.ProfileValue, 200),
                ActualValue = Truncate(reason.ActualValue, 200),
                Outcome = reason.Outcome,
                Rejection = reason.Rejection,
            });
        }

        return record;
    }

    private static string? Truncate(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength];

    /// <summary>
    /// The profile that judges this content kind, falling back to any profile so an install whose
    /// profiles predate scoping still decides rather than silently doing nothing.
    /// </summary>
    private async Task<AcquisitionProfile?> LoadProfileAsync(string appliesTo, CancellationToken cancellationToken)
    {
        var scoped = await ProfileQuery()
            .Where(p => p.AppliesTo == appliesTo)
            .FirstOrDefaultAsync(cancellationToken);

        if (scoped is not null)
        {
            return scoped;
        }

        var fallback = await ProfileQuery().FirstOrDefaultAsync(cancellationToken);
        if (fallback is not null)
        {
            logger.LogWarning(
                "No acquisition profile is scoped to {AppliesTo}; falling back to {ProfileName}, whose size bounds may not suit it.",
                appliesTo, fallback.Name);
        }

        return fallback;
    }

    private IOrderedQueryable<AcquisitionProfile> ProfileQuery() =>
        dbContext.Profiles
            .Include(p => p.AllowedQualities)
            .Include(p => p.FormatRules).ThenInclude(r => r.Conditions)
            .OrderBy(p => p.CreatedAt)
            .ThenBy(p => p.Id); // deterministic pick if two profiles share a timestamp

    private async Task<IReadOnlyDictionary<string, BlockedRelease>> LoadBlocksAsync(CancellationToken cancellationToken)
    {
        var rows = await dbContext.ReleaseBlocks
            .AsNoTracking()
            .Select(b => new BlockedRelease(b.Id, b.ReleaseGuid, b.Reason))
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(b => b.ReleaseGuid, StringComparer.Ordinal);
    }

    /// <summary>
    /// The releases that already failed this goal, keyed by guid, with the reason each failed. Empty for
    /// an evaluation that serves no goal, such as an operator's own interactive search.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>> LoadExclusionsAsync(
        Guid? targetId,
        CancellationToken cancellationToken)
    {
        if (targetId is not { } target)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var rows = await dbContext.ReleaseExclusions
            .AsNoTracking()
            .Where(x => x.TargetId == target)
            .Select(x => new { x.ReleaseGuid, x.Reason })
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(x => x.ReleaseGuid, x => x.Reason, StringComparer.Ordinal);
    }

    private static string? MatchExclusion(IReadOnlyDictionary<string, string> exclusions, string guid)
    {
        var key = Truncate(guid, 500);
        return key is not null && exclusions.TryGetValue(key, out var reason) ? reason : null;
    }

    private static EvaluationReason ExclusionReason(string reason) => new(
        FailedForTargetRule,
        "releaseGuid",
        "not failed for this goal",
        Truncate(reason, 200),
        ReasonOutcome.Fail,
        RejectionKind.Permanent);

    private static BlockedRelease? MatchBlock(IReadOnlyDictionary<string, BlockedRelease> blocks, string guid)
    {
        var key = Truncate(guid, 500);
        return key is not null && blocks.TryGetValue(key, out var block) ? block : null;
    }

    private sealed record BlockedRelease(Guid Id, string ReleaseGuid, string Reason);

    private sealed record Evaluated(
        ReleaseCandidate Candidate,
        ReleaseEvaluationRecord Record,
        EvaluationOutcome Outcome,
        BlockedRelease? Block,
        string? Exclusion = null);

    /// <summary>One evaluated search: every candidate, the one the engine prefers, and what was asked for.</summary>
    private sealed record DecisionRun(
        IReadOnlyList<Evaluated> Evaluations,
        Evaluated? Winner,
        EvaluationRequest? Request);
}
