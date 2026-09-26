using Cinomni.Decision.Contracts;
using Cinomni.Decision.Persistence;
using Cinomni.Operations.Transactions;
using Microsoft.EntityFrameworkCore;

namespace Cinomni.Decision.Application;

public sealed class ProfileQuery(DecisionDbContext dbContext) : IProfileQuery
{
    public async Task<IReadOnlyList<AcquisitionProfileSummary>> ListProfilesAsync(CancellationToken cancellationToken = default)
    {
        var profiles = await dbContext.Profiles
            .AsNoTracking()
            .OrderBy(p => p.CreatedAt)
            .ToListAsync(cancellationToken);

        return profiles
            .Select(p => new AcquisitionProfileSummary(
                new AcquisitionProfileId(p.Id), p.Name, p.MinFormatScore, p.CutoffRank, p.UpgradesAllowed))
            .ToList();
    }
}

/// <summary>
/// The one thing a profile lets an administrator change today: how good is good enough, and whether to
/// go looking at all.
/// </summary>
public sealed class ProfileAdministration(DecisionDbContext dbContext, IUnitOfWork unitOfWork) : IProfileAdministration
{
    public async Task<bool> SetUpgradePolicyAsync(
        Guid profileId,
        int cutoffRank,
        bool upgradesAllowed,
        CancellationToken cancellationToken = default)
    {
        var profile = await dbContext.Profiles.FirstOrDefaultAsync(p => p.Id == profileId, cancellationToken);
        if (profile is null)
        {
            return false;
        }

        await unitOfWork.ExecuteAsync(async token =>
        {
            profile.CutoffRank = cutoffRank;
            profile.UpgradesAllowed = upgradesAllowed;
            await dbContext.SaveChangesAsync(token);
        }, cancellationToken);

        // Nothing is re-judged here. Every title is reassessed as it is imported, and a library-wide
        // sweep triggered by a settings change is a decision for its own increment, not a side effect of
        // moving a slider.
        return true;
    }
}
