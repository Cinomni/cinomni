using Cinomni.Acquisition.Contracts;
using Cinomni.Acquisition.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cinomni.Acquisition.Application;

/// <summary>Read model over the acquisition goals: their current state and full attempt/history trail.</summary>
public sealed class AcquisitionIntentQuery(AcquisitionDbContext dbContext) : IAcquisitionQuery
{
    public async Task<IReadOnlyList<AcquisitionIntentSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        var intents = await dbContext.Intents
            .AsNoTracking()
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync(cancellationToken);

        return intents.Select(ToSummary).ToList();
    }

    public async Task<AcquisitionIntentSummary?> GetByTargetAsync(Guid targetId, CancellationToken cancellationToken = default)
    {
        var intent = await dbContext.Intents
            .AsNoTracking()
            .FirstOrDefaultAsync(i => i.TargetId == targetId, cancellationToken);

        return intent is null ? null : ToSummary(intent);
    }

    public async Task<AcquisitionIntentDetail?> GetAsync(Guid intentId, CancellationToken cancellationToken = default)
    {
        var intent = await dbContext.Intents
            .AsNoTracking()
            .Include(i => i.Attempts)
            .Include(i => i.History)
            .FirstOrDefaultAsync(i => i.Id == intentId, cancellationToken);

        if (intent is null)
        {
            return null;
        }

        var attempts = intent.Attempts
            .OrderBy(a => a.Ordinal)
            .Select(a => new AcquisitionAttemptSummary(
                new AcquisitionAttemptId(a.Id),
                a.Ordinal,
                a.ReleaseGuid,
                a.State,
                a.StartedAt,
                a.ClosedAt,
                a.FailureReason,
                a.ReleaseTitle is null
                    ? null
                    : new AttemptRelease(a.ReleaseTitle, a.IndexerName ?? string.Empty, a.Seeders, a.Leechers)))
            .ToList();

        var history = intent.History
            .OrderBy(h => h.Seq)
            .Select(h => new AcquisitionHistoryEntry(h.Seq, h.FromState, h.ToState, h.Trigger, h.OccurredAt, h.Note))
            .ToList();

        return new AcquisitionIntentDetail(ToSummary(intent), attempts, history);
    }

    internal static AcquisitionIntentSummary ToSummary(AcquisitionIntent intent) => new(
        new AcquisitionIntentId(intent.Id),
        intent.TargetId,
        intent.WorkId,
        intent.State,
        intent.AttemptCount,
        intent.MaxAttempts,
        intent.SelectedReleaseGuid,
        intent.UnitId);
}
