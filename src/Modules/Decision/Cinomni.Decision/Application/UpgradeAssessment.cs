using Cinomni.Decision.Contracts;
using Cinomni.Decision.Evaluation;
using Cinomni.Decision.Persistence;
using Cinomni.Operations.Messaging;
using Cinomni.Operations.Transactions;
using Microsoft.EntityFrameworkCore;

namespace Cinomni.Decision.Application;

/// <summary>
/// Answers, for the units an import just landed, whether they are still worth bettering — and says so,
/// because the sweep needs the answer on every tick and cannot ask Decision each time.
/// <para>
/// Every unit is accounted for, wanting an upgrade or satisfied. Silence about a unit would leave a
/// stale flag standing, and the stale flag that matters is the one on a title that has just been
/// upgraded to the cutoff: left set, it would be searched again every week for ever.
/// </para>
/// </summary>
public sealed class UpgradeAssessment(
    DecisionDbContext dbContext,
    IUnitOfWork unitOfWork,
    IEventBus eventBus,
    CurrentQualityResolver currentQuality)
{
    public async Task AssessAsync(
        Guid assetId,
        Guid workId,
        IReadOnlyList<Guid> unitIds,
        CancellationToken cancellationToken = default)
    {
        if (unitIds.Count == 0)
        {
            return;
        }

        var profile = await LoadProfileAsync(ScopeOf(workId, unitIds), cancellationToken);

        var wanting = new List<Guid>();
        var satisfied = new List<Guid>();

        foreach (var unitId in unitIds)
        {
            if (await WantsUpgradeAsync(profile, unitId, cancellationToken))
            {
                wanting.Add(unitId);
            }
            else
            {
                satisfied.Add(unitId);
            }
        }

        await unitOfWork.ExecuteAsync(
            token => eventBus.PublishAsync(new UpgradeAssessed(assetId, workId, wanting, satisfied), token),
            cancellationToken);
    }

    /// <summary>
    /// Whether this unit's current file sits below the cutoff. Everything that is not a clear "yes" is a
    /// "no": no profile to judge by, upgrades switched off, a quality we cannot read. Each of those is a
    /// reason to leave a working file alone rather than to go looking for a replacement.
    /// </summary>
    private async Task<bool> WantsUpgradeAsync(AcquisitionProfile? profile, Guid unitId, CancellationToken cancellationToken)
    {
        if (profile is not { UpgradesAllowed: true })
        {
            return false;
        }

        var current = await currentQuality.ResolveAsync(profile, [unitId], cancellationToken);
        return current is not null && current.Rank < profile.CutoffRank;
    }

    /// <summary>
    /// Which profile judges this. A movie's asset serves the work itself — that is the documented shape
    /// of the unit ids — so anything else is an episode.
    /// </summary>
    private static string ScopeOf(Guid workId, IReadOnlyList<Guid> unitIds) =>
        unitIds.Contains(workId) ? ProfileScope.Movie : ProfileScope.Series;

    private async Task<AcquisitionProfile?> LoadProfileAsync(string appliesTo, CancellationToken cancellationToken)
    {
        var query = dbContext.Profiles
            .AsNoTracking()
            .Include(p => p.AllowedQualities)
            .OrderBy(p => p.CreatedAt)
            .ThenBy(p => p.Id);

        return await query.FirstOrDefaultAsync(p => p.AppliesTo == appliesTo, cancellationToken)
            ?? await query.FirstOrDefaultAsync(cancellationToken);
    }
}
