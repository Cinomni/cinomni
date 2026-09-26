using Cinomni.Acquisition.Contracts;
using Cinomni.Acquisition.Persistence;
using Cinomni.Kernel.Results;
using Cinomni.Operations.Messaging;
using Cinomni.Operations.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cinomni.Acquisition.Application;

/// <summary>
/// Drives the acquisition goal's lifecycle. Both operations write their aggregate change and publish
/// their events together in one unit of work, and both are idempotent so the at-least-once
/// command queue can safely re-run them.
/// <para>
/// Every path that drops a selection without acting logs why. With a hierarchy, a release resolved
/// to a season goal when only episodes are monitored (or the reverse) would otherwise vanish with no
/// event and no trace, which is close to undiagnosable in production.
/// </para>
/// </summary>
public sealed class AcquisitionService(
    AcquisitionDbContext dbContext,
    IUnitOfWork unitOfWork,
    IEventBus eventBus,
    ILogger<AcquisitionService> logger) : IAcquisitionCommands
{
    // The goal outlives any single download; a handful of attempts before it gives up (case #6).
    private const int DefaultMaxAttempts = 5;

    public async Task CancelWorkGoalsAsync(Guid workId, bool deleteFiles, CancellationToken cancellationToken = default)
    {
        const string Reason = "The work was removed from the catalog.";
        var intents = await LoadWithChildren()
            .Where(i => i.WorkId == workId)
            .ToListAsync(cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var newHistory = new List<AcquisitionHistoryRecord>();
        var cancelled = new List<AcquisitionIntent>();
        foreach (var intent in intents)
        {
            var historyBefore = intent.History.Count;
            if (intent.Cancel(now, Reason))
            {
                newHistory.AddRange(intent.History.Skip(historyBefore));
                cancelled.Add(intent);
            }
        }

        if (cancelled.Count == 0)
        {
            return;
        }

        await unitOfWork.ExecuteAsync(async token =>
        {
            dbContext.History.AddRange(newHistory);
            await dbContext.SaveChangesAsync(token);
            foreach (var intent in cancelled)
            {
                await eventBus.PublishAsync(new AcquisitionCancelled(intent.Id, intent.WorkId, deleteFiles), token);
            }
        }, cancellationToken);

        logger.LogInformation("Cancelled {Count} acquisition goals of removed work {WorkId}.", cancelled.Count, workId);
    }

    public async Task<Result<AcquisitionIntentSummary>> RetryAsync(
        Guid intentId, CancellationToken cancellationToken = default)
    {
        var intent = await LoadAsync(intentId, cancellationToken);
        if (intent is null)
        {
            return Result<AcquisitionIntentSummary>.Failure(
                new Error(AcquisitionErrors.IntentNotFound, "No acquisition goal has this id."));
        }

        if (intent.State is not (IntentState.Searching or IntentState.Exhausted))
        {
            return Result<AcquisitionIntentSummary>.Failure(new Error(
                AcquisitionErrors.NotRetryable,
                intent.State == IntentState.Available
                    ? "This goal already has its content; there is nothing to retry."
                    : "This goal is busy with a download or an import; wait for it to finish or fail."));
        }

        var historyBefore = intent.History.Count;
        if (intent.RetryNow(DateTimeOffset.UtcNow, DefaultMaxAttempts))
        {
            var newHistory = intent.History.Skip(historyBefore).ToList();
            await unitOfWork.ExecuteAsync(async token =>
            {
                // Same explicit-Add as a selection: history rows reached only through the navigation
                // carry client-assigned keys and would otherwise be tracked as Modified.
                dbContext.History.AddRange(newHistory);
                await dbContext.SaveChangesAsync(token);
            }, cancellationToken);

            logger.LogInformation(
                "Goal {IntentId} reopened by an administrator after {AttemptCount} attempts.", intent.Id, intent.AttemptCount);
        }

        return Result<AcquisitionIntentSummary>.Success(AcquisitionIntentQuery.ToSummary(intent));
    }

    public async Task<AcquisitionIntentId> CreateIntentAsync(
        Guid targetId,
        Guid workId,
        string mode,
        Guid? unitId = null,
        CancellationToken cancellationToken = default)
    {
        // Idempotent per target: one persistent goal per monitored target. A redelivered
        // MonitoringEnabled (or a re-enable) reuses the existing intent rather than forking it.
        var existing = await dbContext.Intents
            .FirstOrDefaultAsync(i => i.TargetId == targetId, cancellationToken);
        if (existing is not null)
        {
            return new AcquisitionIntentId(existing.Id);
        }

        var now = DateTimeOffset.UtcNow;
        var intent = AcquisitionIntent.Create(targetId, workId, mode, now, DefaultMaxAttempts, unitId);
        intent.Plan(now); // Requested → Planned → Searching

        await unitOfWork.ExecuteAsync(async token =>
        {
            dbContext.Intents.Add(intent);
            await dbContext.SaveChangesAsync(token);
            await eventBus.PublishAsync(new AcquisitionRequested(intent.Id, workId, targetId), token);
        }, cancellationToken);

        return new AcquisitionIntentId(intent.Id);
    }

    public async Task SelectCandidateAsync(
        Guid evaluationId,
        Guid targetId,
        string releaseGuid,
        string downloadUrl,
        IReadOnlyList<Guid>? unitIds = null,
        AttemptRelease? release = null,
        CancellationToken cancellationToken = default)
    {
        var intent = await ResolveGoalAsync(targetId, unitIds, cancellationToken);

        // No goal for this target or any of its units (its MonitoringEnabled hasn't been processed,
        // or the release resolved to units nobody is monitoring). The selection can't be routed.
        if (intent is null)
        {
            logger.LogWarning(
                "Dropping selection {EvaluationId} ({ReleaseGuid}): no acquisition goal for target {TargetId} or units [{UnitIds}].",
                evaluationId, releaseGuid, targetId, string.Join(',', unitIds ?? []));
            return;
        }

        // Idempotent per evaluation: if this selection already opened an attempt, do nothing.
        if (intent.Attempts.Any(a => a.EvaluationId == evaluationId))
        {
            return;
        }

        // Only a searchable goal takes a new candidate — a download already in flight is left alone.
        if (!intent.CanSelectCandidate)
        {
            if (intent.HasGivenUp)
            {
                // Resolution already looked for a sibling goal that could take it and found none, so
                // this release is lost for good. That must never be a quiet Information line: the
                // sweep keeps finding the same release every cooldown and discarding it forever.
                logger.LogWarning(
                    "Dropping selection {EvaluationId} ({ReleaseGuid}) for target {TargetId}: goal {IntentId} " +
                    "is Exhausted after {AttemptCount} attempts and no goal for units [{UnitIds}] can accept it.",
                    evaluationId, releaseGuid, targetId, intent.Id, intent.AttemptCount,
                    string.Join(',', unitIds ?? []));
                return;
            }

            logger.LogInformation(
                "Skipping selection {EvaluationId} for goal {IntentId}: it is {State}, not searchable.",
                evaluationId, intent.Id, intent.State);
            return;
        }

        var claimedUnits = UnitsOf(intent, unitIds);
        var historyBefore = intent.History.Count;
        var attempt = intent.SelectCandidate(evaluationId, releaseGuid, downloadUrl, DateTimeOffset.UtcNow, release);
        attempt.ClaimUnits(claimedUnits);
        var newHistory = intent.History.Skip(historyBefore).ToList();

        await unitOfWork.ExecuteAsync(async token =>
        {
            // The intent is tracked (loaded), so its scalar changes UPDATE correctly. New children
            // reached only through its navigations would be mis-tracked as Modified (they carry
            // client-assigned keys), so add them to their sets explicitly — the same explicit-Add
            // the other modules use. On a freshly created intent the Added root cascades instead.
            dbContext.Attempts.Add(attempt);
            dbContext.AttemptUnits.AddRange(attempt.Units);
            dbContext.History.AddRange(newHistory);
            await dbContext.SaveChangesAsync(token);
            await eventBus.PublishAsync(
                new CandidateSelected(intent.Id, intent.TargetId, evaluationId, attempt.ReleaseGuid), token);
            await eventBus.PublishAsync(
                new DownloadQueued(
                    attempt.Id, intent.Id, intent.WorkId, intent.TargetId, attempt.ReleaseGuid, attempt.DownloadUrl,
                    SavePath: null, UnitIds: claimedUnits),
                token);
        }, cancellationToken);
    }

    public async Task MarkUnitsSatisfiedAsync(
        IReadOnlyList<Guid> unitIds,
        Guid assetId,
        CancellationToken cancellationToken = default)
    {
        if (unitIds.Count == 0)
        {
            return;
        }

        // A purely local query: Acquisition owns acquisition_intents.unit_id, so closing the goals a
        // landed asset satisfied needs no cross-module call.
        var intents = await dbContext.Intents
            .Include(i => i.Attempts)
            .Include(i => i.History)
            .Where(i => i.UnitId != null && unitIds.Contains(i.UnitId!.Value))
            .ToListAsync(cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var newHistory = new List<AcquisitionHistoryRecord>();
        var met = new List<AcquisitionIntent>();

        foreach (var intent in intents)
        {
            var historyBefore = intent.History.Count;
            if (!Satisfy(intent, assetId, now))
            {
                continue;
            }

            newHistory.AddRange(intent.History.Skip(historyBefore));
            met.Add(intent);
        }

        if (met.Count == 0)
        {
            return; // every matching goal was already met, or has its own download still in flight
        }

        await unitOfWork.ExecuteAsync(async token =>
        {
            dbContext.History.AddRange(newHistory);
            await dbContext.SaveChangesAsync(token);
            foreach (var intent in met)
            {
                await eventBus.PublishAsync(new AcquisitionSucceeded(intent.Id, intent.WorkId), token);
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Closes one goal whose unit has landed. A goal that never opened an attempt is satisfied
    /// externally (no attempt consumed); a goal already awaiting <em>this</em> import is closed the
    /// ordinary way, so its attempt is recorded as Imported rather than left open forever.
    /// </summary>
    private static bool Satisfy(AcquisitionIntent intent, Guid assetId, DateTimeOffset now)
    {
        // Importing is only reachable through an attempt, but this closes a whole batch of goals in
        // one unit of work: an unexpectedly attempt-less goal must not throw and roll the rest back.
        if (intent.State == IntentState.Importing && intent.Attempts.Count > 0)
        {
            intent.MarkImported(now);
            return true;
        }

        return intent.MarkSatisfiedExternally(assetId, now);
    }

    /// <summary>
    /// Finds the goal a selection belongs to: by target first (the ordinary route), then by any of
    /// the units the release covers. The unit route is what lets a pack chosen for a season goal
    /// still land when only the episodes underneath it are monitored.
    /// <para>
    /// A goal that has <b>given up</b> does not get to block the release either. The season target is
    /// the one the sweep keeps planning packs for, so once its goal exhausts its attempts every
    /// future pack would resolve to it and be discarded — while the ten episode goals underneath it
    /// sit in <c>Searching</c>, perfectly able to take the very same release. There is no way back
    /// for the exhausted goal (<c>create-intent:{targetId}</c> is spent forever), so the fall-through
    /// is the only route left. A goal that is merely <em>busy</em> (a download of its own in flight)
    /// is NOT fallen through: that would open a second download for content already on its way.
    /// </para>
    /// </summary>
    private async Task<AcquisitionIntent?> ResolveGoalAsync(
        Guid targetId,
        IReadOnlyList<Guid>? unitIds,
        CancellationToken cancellationToken)
    {
        var byTarget = await LoadWithChildren()
            .FirstOrDefaultAsync(i => i.TargetId == targetId, cancellationToken);
        if (unitIds is not { Count: > 0 } || (byTarget is not null && !byTarget.HasGivenUp))
        {
            return byTarget;
        }

        var byUnit = await LoadWithChildren()
            .Where(i => i.UnitId != null && unitIds.Contains(i.UnitId!.Value))
            .OrderBy(i => i.CreatedAt)
            .ThenBy(i => i.Id) // deterministic when several units of one release have goals
            .ToListAsync(cancellationToken);

        // Only a goal that can actually take the candidate is worth falling through to; otherwise
        // keep the by-target goal so the caller reports the drop against the goal Decision aimed at.
        return byUnit.FirstOrDefault(i => i.CanSelectCandidate) ?? byTarget ?? byUnit.FirstOrDefault();
    }

    private IQueryable<AcquisitionIntent> LoadWithChildren() =>
        dbContext.Intents
            .Include(i => i.Attempts)
            .Include(i => i.History);

    public async Task MarkDownloadStartedAsync(Guid intentId, CancellationToken cancellationToken = default)
    {
        var intent = await LoadAsync(intentId, cancellationToken);
        if (intent is null || intent.State != IntentState.Downloading)
        {
            return; // out-of-order or already advanced — nothing to confirm
        }

        // Only the current attempt is mutated (no new history), so a plain save suffices.
        intent.MarkDownloadStarted(DateTimeOffset.UtcNow);
        await unitOfWork.ExecuteAsync(dbContext.SaveChangesAsync, cancellationToken);
    }

    public async Task MarkDownloadCompletedAsync(Guid intentId, CancellationToken cancellationToken = default)
    {
        var intent = await LoadAsync(intentId, cancellationToken);
        if (intent is null || intent.State != IntentState.Downloading)
        {
            return;
        }

        var historyBefore = intent.History.Count;
        intent.MarkDownloadCompleted(DateTimeOffset.UtcNow); // → Importing (awaiting Import)
        var newHistory = intent.History.Skip(historyBefore).ToList();

        await unitOfWork.ExecuteAsync(async token =>
        {
            dbContext.History.AddRange(newHistory);
            await dbContext.SaveChangesAsync(token);
        }, cancellationToken);
    }

    /// <summary>The attempt that just closed, named for Decision so it stops offering that release to this goal.</summary>
    private static AcquisitionAttemptFailed AttemptFailedOf(AcquisitionIntent intent, string reason)
    {
        var attempt = intent.Attempts[^1];
        return new AcquisitionAttemptFailed(
            intent.Id, intent.TargetId, attempt.Id, attempt.ReleaseGuid, reason.Length > 200 ? reason[..200] : reason);
    }

    public async Task MarkDownloadNotStartedAsync(
        Guid intentId, Guid attemptId, string reason, CancellationToken cancellationToken = default)
    {
        var intent = await LoadAsync(intentId, cancellationToken);
        if (intent is null || intent.State != IntentState.Downloading || intent.Attempts.LastOrDefault()?.Id != attemptId)
        {
            return; // moved on, or a report about an attempt that is no longer the current one
        }

        await MarkDownloadFailedAsync(intentId, reason, cancellationToken);
    }

    public async Task MarkDownloadFailedAsync(Guid intentId, string reason, CancellationToken cancellationToken = default)
    {
        var intent = await LoadAsync(intentId, cancellationToken);
        if (intent is null || intent.State != IntentState.Downloading)
        {
            return;
        }

        var historyBefore = intent.History.Count;
        intent.MarkDownloadFailed(reason, DateTimeOffset.UtcNow); // closes the attempt, then retry-or-exhaust
        var newHistory = intent.History.Skip(historyBefore).ToList();

        await unitOfWork.ExecuteAsync(async token =>
        {
            dbContext.History.AddRange(newHistory);
            await dbContext.SaveChangesAsync(token);

            await eventBus.PublishAsync(AttemptFailedOf(intent, reason), token);

            // The goal survives a download failure: it either reopened for search or exhausted.
            if (intent.State == IntentState.Exhausted)
            {
                await eventBus.PublishAsync(
                    new AcquisitionFailed(intent.Id, intent.WorkId, reason, intent.AttemptCount), token);
            }
            else if (intent.State == IntentState.Searching)
            {
                await eventBus.PublishAsync(
                    new AcquisitionRetrying(intent.Id, intent.TargetId, reason, intent.AttemptCount + 1), token);
            }
        }, cancellationToken);
    }

    public async Task MarkImportedAsync(Guid intentId, CancellationToken cancellationToken = default)
    {
        var intent = await LoadAsync(intentId, cancellationToken);
        if (intent is null || intent.State != IntentState.Importing)
        {
            return; // out-of-order or already advanced — nothing to meet
        }

        var historyBefore = intent.History.Count;
        intent.MarkImported(DateTimeOffset.UtcNow); // → Available (the goal is met)
        var newHistory = intent.History.Skip(historyBefore).ToList();

        await unitOfWork.ExecuteAsync(async token =>
        {
            dbContext.History.AddRange(newHistory);
            await dbContext.SaveChangesAsync(token);
            await eventBus.PublishAsync(new AcquisitionSucceeded(intent.Id, intent.WorkId), token);
        }, cancellationToken);
    }

    public async Task MarkImportFailedAsync(Guid intentId, string reason, CancellationToken cancellationToken = default)
    {
        var intent = await LoadAsync(intentId, cancellationToken);
        if (intent is null || intent.State != IntentState.Importing)
        {
            return;
        }

        var historyBefore = intent.History.Count;
        intent.MarkImportFailed(reason, DateTimeOffset.UtcNow); // closes the attempt, then retry-or-exhaust
        var newHistory = intent.History.Skip(historyBefore).ToList();

        await unitOfWork.ExecuteAsync(async token =>
        {
            dbContext.History.AddRange(newHistory);
            await dbContext.SaveChangesAsync(token);

            await eventBus.PublishAsync(AttemptFailedOf(intent, reason), token);

            // The goal survives an import failure: it either reopened for search or exhausted.
            if (intent.State == IntentState.Exhausted)
            {
                await eventBus.PublishAsync(
                    new AcquisitionFailed(intent.Id, intent.WorkId, reason, intent.AttemptCount), token);
            }
            else if (intent.State == IntentState.Searching)
            {
                await eventBus.PublishAsync(
                    new AcquisitionRetrying(intent.Id, intent.TargetId, reason, intent.AttemptCount + 1), token);
            }
        }, cancellationToken);
    }

    /// <summary>
    /// The catalog units an attempt claims. Decision supplies them once it can resolve a release to
    /// episodes; failing that the goal's own unit is used, and failing that its work id — which is
    /// exactly the movie unit, so the published payload stays movie-identical.
    /// </summary>
    private static IReadOnlyList<Guid> UnitsOf(AcquisitionIntent intent, IReadOnlyList<Guid>? unitIds)
    {
        if (unitIds is { Count: > 0 })
        {
            return unitIds;
        }

        return intent.UnitId is Guid unit ? [unit] : [intent.WorkId];
    }

    private Task<AcquisitionIntent?> LoadAsync(Guid intentId, CancellationToken cancellationToken) =>
        dbContext.Intents
            .Include(i => i.Attempts)
            .Include(i => i.History)
            .FirstOrDefaultAsync(i => i.Id == intentId, cancellationToken);
}
