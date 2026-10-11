using Cinomni.Kernel.Results;
using Cinomni.Operations.Transactions;
using Cinomni.Subtitles.Contracts;
using Cinomni.Subtitles.Files;
using Cinomni.Subtitles.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cinomni.Subtitles.Sync;

/// <summary>
/// What an administrator can do about a subtitle's timing: ask for it to be checked again, or put the
/// downloaded file back. A concrete class bound only by this module's endpoints, which are
/// administrator-only.
/// </summary>
public sealed class SubtitleSyncCommands(
    SubtitlesDbContext dbContext,
    IUnitOfWork unitOfWork,
    ISubtitleFileStore fileStore,
    ILogger<SubtitleSyncCommands> logger,
    SubtitleSyncOptions? options = null)
{
    public static readonly Error NotFound = new("subtitles.subtitle_not_found", "No such subtitle.");

    public static readonly Error InProgress = new(
        "subtitles.sync_in_progress", "This subtitle is being synchronized. Wait for the result, then try again.");

    public static readonly Error NotSynced = new(
        "subtitles.not_synced", "This subtitle already plays the downloaded file; there is nothing to revert.");

    public static readonly Error Disabled = new(
        "subtitles.sync_disabled", "Subtitle synchronization is turned off (Subtitles:Sync:Enabled).");

    public static readonly Error NotDownloaded = new(
        "subtitles.not_downloaded", "This subtitle did not finish landing, so it cannot be synchronized yet.");

    /// <summary>
    /// Queues the subtitle for another pass over its original file. The subtitle keeps playing what it
    /// plays now until the pass decides.
    /// </summary>
    public async Task<Result> ResyncAsync(SubtitleAssetId id, CancellationToken cancellationToken = default)
    {
        if (options is not { Enabled: true })
        {
            return Result.Failure(Disabled);
        }

        var now = DateTimeOffset.UtcNow;
        var result = Result.Failure(NotFound);
        try
        {
            await unitOfWork.ExecuteAsync(async token =>
            {
                // Decided under the subtitle's lock, so a revert or a pass completing at the same moment is
                // either already visible here or waits for this to commit.
                var subtitle = await dbContext.LockAsync(id.Value, token);
                var search = subtitle is null
                    ? null
                    : await dbContext.Searches.FirstOrDefaultAsync(row => row.Id == subtitle.SearchId, token);
                if (subtitle is null || search is null)
                {
                    result = Result.Failure(NotFound);
                    return;
                }

                if (search.State == SubtitleSearchState.Syncing)
                {
                    result = Result.Failure(InProgress);
                    return;
                }

                if (search.State != SubtitleSearchState.Available || search.SubtitleAssetId != subtitle.Id)
                {
                    result = Result.Failure(NotDownloaded);
                    return;
                }

                search.BeginResync(now);
                dbContext.SyncWork.Add(SubtitleSyncWork.Create(subtitle.Id, search.Id, now));
                await dbContext.SaveChangesAsync(token);
                result = Result.Success();
            }, cancellationToken);
        }
        catch (DbUpdateException)
        {
            // A pending row for this subtitle already exists (its key is the subtitle), so only one pass
            // is ever pending.
            return Result.Failure(InProgress);
        }

        return result;
    }

    /// <summary>Plays the downloaded file again and records that an administrator chose it.</summary>
    public async Task<Result> RevertAsync(SubtitleAssetId id, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var result = Result.Failure(NotFound);
        (string Path, string Beside)? released = null;
        await unitOfWork.ExecuteAsync(async token =>
        {
            // Checked and changed under the subtitle's lock. Outside it, a re-sync could queue between the
            // check and the commit and its pass would then quietly undo this revert; and of two reverts at
            // once, the second now finds nothing left to revert instead of recording a second one.
            var subtitle = await dbContext.LockAsync(id.Value, token);
            if (subtitle is null)
            {
                result = Result.Failure(NotFound);
                return;
            }

            var syncing = await dbContext.Searches.AnyAsync(
                row => row.Id == subtitle.SearchId && row.State == SubtitleSearchState.Syncing, token);
            if (syncing)
            {
                result = Result.Failure(InProgress);
                return;
            }

            if (!subtitle.IsSynced)
            {
                result = Result.Failure(NotSynced);
                return;
            }

            released = (subtitle.Path, subtitle.OriginalPath);
            subtitle.UseOriginal();
            dbContext.SyncDecisions.Add(SubtitleSyncDecision.Unmeasured(
                subtitle.Id, SubtitleSyncOutcome.Reverted, "Reverted to the downloaded file by an administrator.", now));
            await dbContext.SaveChangesAsync(token);
            result = Result.Success();
        }, cancellationToken);

        if (released is { } copy)
        {
            try
            {
                // Every pass writes under a name of its own, so this is the copy that was being played
                // and never one a later pass has written since.
                await fileStore.DeleteSyncedAsync(copy.Path, copy.Beside, CancellationToken.None);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Nothing points at it any more; a leftover copy is untidy, not wrong.
                logger.LogWarning(ex, "The synchronized copy of subtitle {SubtitleId} could not be removed.", id.Value);
            }
        }

        return result;
    }
}
