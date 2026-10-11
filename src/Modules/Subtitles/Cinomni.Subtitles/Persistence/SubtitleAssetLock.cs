using Microsoft.EntityFrameworkCore;

namespace Cinomni.Subtitles.Persistence;

/// <summary>
/// The one lock every change to which file a subtitle plays takes: a sync pass recording its verdict,
/// Import's relocation, an administrator's revert and a re-sync request. Each holds the subtitle's row
/// until it commits, so they run one after the other instead of reading the same row and writing over
/// each other — a relocation that read the paths before a pass committed would otherwise put the old
/// file back, and a pass that read them before a relocation would point at a directory nothing is in.
/// <para>
/// Taken first in the unit of work, and followed by a fresh read of the row: what was loaded before the
/// lock is what the other writer may just have changed.
/// </para>
/// </summary>
internal static class SubtitleAssetLock
{
    /// <summary>Locks the subtitle's row for the rest of the transaction and returns it as it now stands.</summary>
    public static async Task<SubtitleAsset?> LockAsync(
        this SubtitlesDbContext dbContext, Guid subtitleAssetId, CancellationToken cancellationToken)
    {
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM subtitles.subtitle_assets WHERE id = {subtitleAssetId} FOR UPDATE", cancellationToken);

        var subtitle = await dbContext.Assets.FirstOrDefaultAsync(asset => asset.Id == subtitleAssetId, cancellationToken);
        if (subtitle is not null)
        {
            await dbContext.Entry(subtitle).ReloadAsync(cancellationToken);
        }

        return subtitle;
    }
}
