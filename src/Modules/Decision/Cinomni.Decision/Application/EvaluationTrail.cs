using Cinomni.Decision.Contracts;
using Cinomni.Decision.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cinomni.Decision.Application;

/// <summary>
/// Appends to an evaluation's explanation after the fact — a block, its lifting, a manual override.
/// The trail is append-only and numbered, so two writers that each read "the next number is 7" would
/// collide on the key and one of them would fail. Every appender therefore locks the evaluation row
/// first and reads the trail under that lock; the second writer waits and then sees the first one's line.
/// </summary>
internal static class EvaluationTrail
{
    /// <summary>The line that says a block recorded on this evaluation no longer stands.</summary>
    internal const string UnblockedRule = "ReleaseUnblocked";

    private static readonly string LockSql =
        $"SELECT id AS \"Value\" FROM {DecisionDbContext.SchemaName}.release_evaluations WHERE id = {{0}} FOR UPDATE";

    /// <summary>
    /// Locks the evaluation and returns its trail as it stands, oldest first; null when the evaluation
    /// is gone (retention purged it since the caller read it). Must run inside the caller's unit of
    /// work: the lock lasts until that transaction ends.
    /// </summary>
    public static async Task<IReadOnlyList<DecisionReasonRecord>?> LockAsync(
        DecisionDbContext dbContext,
        Guid evaluationId,
        CancellationToken cancellationToken)
    {
        var locked = await dbContext.Database
            .SqlQueryRaw<Guid>(LockSql, evaluationId)
            .ToListAsync(cancellationToken);
        if (locked.Count == 0)
        {
            return null;
        }

        return await dbContext.DecisionReasons
            .AsNoTracking()
            .Where(r => r.EvaluationId == evaluationId)
            .OrderBy(r => r.Seq)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Adds one line after the last one in <paramref name="trail"/>, which must have been read under the lock.</summary>
    public static void Append(
        DecisionDbContext dbContext,
        Guid evaluationId,
        IReadOnlyList<DecisionReasonRecord> trail,
        string rule,
        string property,
        string profileValue,
        string actualValue,
        ReasonOutcome outcome,
        RejectionKind? rejection)
    {
        dbContext.DecisionReasons.Add(new DecisionReasonRecord
        {
            EvaluationId = evaluationId,
            Seq = trail.Count == 0 ? 0 : trail.Max(r => r.Seq) + 1,
            Rule = rule,
            Property = property,
            ProfileValue = profileValue,
            ActualValue = actualValue.Length <= 200 ? actualValue : actualValue[..200],
            Outcome = outcome,
            Rejection = rejection,
        });
    }

    /// <summary>
    /// Whether taking this release goes against what the profile said. A release that failed only
    /// because it was blocked does not: a grab refuses while the block stands, so once one gets through
    /// the block is gone, and grabbing what the profile accepted overrides nothing.
    /// </summary>
    public static bool GrabOverridesVerdict(Verdict verdict, IEnumerable<(string Rule, ReasonOutcome Outcome)> reasons) =>
        verdict != Verdict.Accepted
        && reasons.Any(r => r.Outcome == ReasonOutcome.Fail && r.Rule != ReleaseBlocklist.BlockedRule);

    /// <summary>
    /// Whether the trail's last word on blocking is that the release is blocked. A block can be lifted
    /// and laid again, so it is the latest of the two lines that counts, not whether a block line exists.
    /// </summary>
    public static bool EndsBlocked(IEnumerable<DecisionReasonRecord> trail) =>
        trail.LastOrDefault(r => r.Rule is ReleaseBlocklist.BlockedRule or UnblockedRule)?.Rule
            == ReleaseBlocklist.BlockedRule;
}
