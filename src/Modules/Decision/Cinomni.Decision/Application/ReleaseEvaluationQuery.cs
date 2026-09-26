using Cinomni.Decision.Contracts;
using Cinomni.Decision.Persistence;
using Cinomni.Discovery.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Cinomni.Decision.Application;

public sealed class ReleaseEvaluationQuery(
    DecisionDbContext dbContext,
    // Optional so a composition without Discovery still answers; the evaluations then carry no swarm.
    IReleaseSearchResults? searchResults = null) : IReleaseEvaluationQuery
{
    /// <summary>
    /// How many of the most recent searches have their releases read back from Discovery. The history is
    /// read newest first and a person looks at the latest few; reading every search a target ever had
    /// would cost one query per sweep for months of them.
    /// </summary>
    private const int DescribedSearches = 5;

    public async Task<IReadOnlyList<ReleaseEvaluationSummary>> GetForTargetAsync(
        Guid targetId,
        CancellationToken cancellationToken = default)
    {
        var records = await dbContext.ReleaseEvaluations
            .AsNoTracking()
            .Include(e => e.Reasons)
            .Where(e => e.TargetId == targetId)
            .OrderByDescending(e => e.CreatedAt)
            .ToListAsync(cancellationToken);

        var releases = await DescribeAsync(records, cancellationToken);
        return records
            .Select(record => ToSummary(record, releases.GetValueOrDefault((record.SearchId, record.ReleaseGuid))))
            .ToList();
    }

    /// <summary>
    /// The releases of the most recent searches as the indexers described them, by search and release.
    /// A search whose results were already purged simply describes nothing.
    /// </summary>
    private async Task<Dictionary<(Guid SearchId, string ReleaseGuid), ReleaseCandidate>> DescribeAsync(
        IReadOnlyList<ReleaseEvaluationRecord> records,
        CancellationToken cancellationToken)
    {
        var described = new Dictionary<(Guid, string), ReleaseCandidate>();
        if (searchResults is null)
        {
            return described;
        }

        foreach (var searchId in records.Select(r => r.SearchId).Distinct().Take(DescribedSearches))
        {
            var results = await searchResults.GetResultsAsync(new SearchExecutionId(searchId), cancellationToken);
            foreach (var candidate in results)
            {
                described.TryAdd((searchId, candidate.Guid), candidate);
            }
        }

        return described;
    }

    private static ReleaseEvaluationSummary ToSummary(ReleaseEvaluationRecord record, ReleaseCandidate? release) => new(
        new ReleaseEvaluationId(record.Id),
        record.ReleaseGuid,
        record.ReleaseTitle,
        record.Verdict,
        record.CustomFormatScore,
        record.Reasons
            .OrderBy(r => r.Seq)
            .Select(r => new EvaluationReason(r.Rule, r.Property, r.ProfileValue, r.ActualValue, r.Outcome, r.Rejection))
            .ToList(),
        release?.IndexerName,
        release?.Seeders,
        release?.Leechers);
}
