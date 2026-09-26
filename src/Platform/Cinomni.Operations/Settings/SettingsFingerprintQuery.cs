using Cinomni.Operations.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cinomni.Operations.Settings;

/// <summary>One cheap statement the drift poll runs every tick before deciding whether to reload.</summary>
internal static class SettingsFingerprintQuery
{
    public static async Task<SettingsFingerprint> ComputeAsync(
        OperationsDbContext dbContext, CancellationToken cancellationToken)
    {
        var row = await dbContext.Settings
            .AsNoTracking()
            .GroupBy(_ => 1)
            .Select(g => new SettingsFingerprint(
                g.Count(),
                g.Max(s => (long?)s.Version),
                g.Max(s => (DateTimeOffset?)s.UpdatedAt)))
            .SingleOrDefaultAsync(cancellationToken);

        return row ?? SettingsFingerprint.Empty;
    }
}
