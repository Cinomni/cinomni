using Cinomni.Decision.Contracts;
using Cinomni.Decision.Persistence;
using Cinomni.Kernel.Identifiers;
using Cinomni.Kernel.Results;
using Cinomni.Operations.Transactions;
using Microsoft.EntityFrameworkCore;

namespace Cinomni.Decision.Application;

/// <summary>
/// Persists which releases the sweep must skip, and why. The identity is the evaluation's release
/// guid — the same key Discovery deduplicates on — so a caller cannot name a release this installation
/// has never seen.
/// </summary>
public sealed class ReleaseBlocklist(DecisionDbContext dbContext, IUnitOfWork unitOfWork) : IReleaseBlocklist
{
    internal const string BlockedRule = "ReleaseBlocked";

    public const int MaxReasonLength = 500;

    /// <summary>The list the console reads. One past the cap is how a full page says it may not be all of them.</summary>
    public const int ListCap = 100;

    public async Task<Result<ReleaseBlock>> BlockAsync(
        Guid evaluationId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var trimmed = reason?.Trim() ?? string.Empty;
        if (trimmed.Length is 0 or > MaxReasonLength)
        {
            return Result<ReleaseBlock>.Failure(new Error(
                "decision.invalid_block_reason",
                "A block needs a reason, of at most 500 characters."));
        }

        var evaluation = await dbContext.ReleaseEvaluations
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == evaluationId, cancellationToken);
        if (evaluation is null)
        {
            return Result<ReleaseBlock>.Failure(new Error(
                "decision.evaluation_not_found", "No such release evaluation."));
        }

        var existing = await dbContext.ReleaseBlocks
            .FirstOrDefaultAsync(b => b.ReleaseGuid == evaluation.ReleaseGuid, cancellationToken);
        if (existing is not null)
        {
            existing.Reason = trimmed;
            existing.ReleaseTitle = evaluation.ReleaseTitle;
            await unitOfWork.ExecuteAsync(async token =>
            {
                await NoteOnEvaluationAsync(evaluation.Id, trimmed, token);
                await dbContext.SaveChangesAsync(token);
            }, cancellationToken);
            return Result<ReleaseBlock>.Success(ToContract(existing));
        }

        var block = new ReleaseBlockRecord
        {
            Id = Uuid7.New(),
            ReleaseGuid = evaluation.ReleaseGuid,
            ReleaseTitle = evaluation.ReleaseTitle,
            Reason = trimmed,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        try
        {
            await unitOfWork.ExecuteAsync(async token =>
            {
                await NoteOnEvaluationAsync(evaluation.Id, trimmed, token);
                dbContext.ReleaseBlocks.Add(block);
                await dbContext.SaveChangesAsync(token);
            }, cancellationToken);
        }
        catch (DbUpdateException)
        {
            var winner = await dbContext.ReleaseBlocks.AsNoTracking()
                .SingleOrDefaultAsync(b => b.ReleaseGuid == evaluation.ReleaseGuid, cancellationToken);
            if (winner is null)
            {
                throw;
            }

            return Result<ReleaseBlock>.Success(ToContract(winner));
        }

        return Result<ReleaseBlock>.Success(ToContract(block));
    }

    public async Task<Result> UnblockAsync(Guid blockId, CancellationToken cancellationToken = default)
    {
        var block = await dbContext.ReleaseBlocks.FirstOrDefaultAsync(b => b.Id == blockId, cancellationToken);
        if (block is null)
        {
            return Result.Success();
        }

        // Every evaluation whose trail ends on this block is told the block is gone, in the same commit as
        // the removal: the trail is append-only, so the lifting is a line of its own, and a person reading
        // the evaluation afterwards sees both why it was set aside and that it no longer is. The list is
        // read after the delete, inside the same transaction, so a sweep that committed a blocked
        // evaluation a moment ago is not missed.
        dbContext.ReleaseBlocks.Remove(block);
        await unitOfWork.ExecuteAsync(async token =>
        {
            await dbContext.SaveChangesAsync(token);

            var noted = await dbContext.ReleaseEvaluations
                .AsNoTracking()
                .Where(e => e.ReleaseGuid == block.ReleaseGuid
                    && e.Reasons.Any(r => r.Rule == BlockedRule))
                .OrderBy(e => e.Id)
                .Select(e => e.Id)
                .ToListAsync(token);

            foreach (var evaluationId in noted)
            {
                var trail = await EvaluationTrail.LockAsync(dbContext, evaluationId, token);
                if (trail is not null && EvaluationTrail.EndsBlocked(trail))
                {
                    EvaluationTrail.Append(
                        dbContext, evaluationId, trail, EvaluationTrail.UnblockedRule, "releaseGuid",
                        "not blocked", "unblocked by an administrator", ReasonOutcome.Pass, rejection: null);
                }
            }

            await dbContext.SaveChangesAsync(token);
        }, cancellationToken);
        return Result.Success();
    }

    public async Task<ReleaseBlockList> ListAsync(CancellationToken cancellationToken = default)
    {
        var rows = await dbContext.ReleaseBlocks
            .AsNoTracking()
            .OrderByDescending(b => b.CreatedAt)
            .ThenByDescending(b => b.Id)
            .Take(ListCap + 1)
            .ToListAsync(cancellationToken);

        var truncated = rows.Count > ListCap;
        if (truncated)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        return new ReleaseBlockList(rows.Select(ToContract).ToList(), truncated);
    }

    /// <summary>
    /// Records the block on the evaluation that was blocked, so that search's explainability trail
    /// says why this release was set aside. The block row is what the next sweep reads; this reason
    /// is what a person reads on the evaluation itself. Taken under the evaluation's lock, so a grab
    /// appending its override at the same moment cannot claim the same line number.
    /// </summary>
    private async Task NoteOnEvaluationAsync(Guid evaluationId, string reason, CancellationToken cancellationToken)
    {
        // Gone (purged since it was read): the block row still stands, there is just no trail to note it on.
        var trail = await EvaluationTrail.LockAsync(dbContext, evaluationId, cancellationToken);
        if (trail is null || EvaluationTrail.EndsBlocked(trail))
        {
            return;
        }

        EvaluationTrail.Append(
            dbContext, evaluationId, trail, BlockedRule, "releaseGuid", "not blocked", reason,
            ReasonOutcome.Fail, RejectionKind.Permanent);
    }

    private static ReleaseBlock ToContract(ReleaseBlockRecord block) =>
        new(block.Id, block.ReleaseGuid, block.ReleaseTitle, block.Reason, block.CreatedAt);
}
