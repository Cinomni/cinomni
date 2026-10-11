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

        var subtitle = await dbContext.Assets.AsNoTracking().FirstOrDefaultAsync(asset => asset.Id == id.Value, cancellationToken);
        if (subtitle is null)
        {
            return Result.Failure(NotFound);
        }

        var search = await dbContext.Searches.FirstOrDefaultAsync(row => row.Id == subtitle.SearchId, cancellationToken);
        if (search is null)
        {
            return Result.Failure(NotFound);
        }

        if (search.State == SubtitleSearchState.Syncing)
        {
            return Result.Failure(InProgress);
        }

        if (search.State != SubtitleSearchState.Available || search.SubtitleAssetId != subtitle.Id)
        {
            return Result.Failure(NotDownloaded);
        }

        var now = DateTimeOffset.UtcNow;
        try
        {
            await unitOfWork.ExecuteAsync(async token =>
            {
                search.BeginResync(now);
                dbContext.SyncWork.Add(SubtitleSyncWork.Create(subtitle.Id, search.Id, now));
                await dbContext.SaveChangesAsync(token);
            }, cancellationToken);
        }
        catch (DbUpdateException)
        {
            // A second request raced this one and queued the same subtitle first: the row's key is the
            // subtitle, so only one pass is ever pending.
            return Result.Failure(InProgress);
        }

        return Result.Success();
    }

    /// <summary>Plays the downloaded file again and records that an administrator chose it.</summary>
    public async Task<Result> RevertAsync(SubtitleAssetId id, CancellationToken cancellationToken = default)
    {
        var subtitle = await dbContext.Assets.FirstOrDefaultAsync(asset => asset.Id == id.Value, cancellationToken);
        if (subtitle is null)
        {
            return Result.Failure(NotFound);
        }

        var syncing = await dbContext.Searches.AnyAsync(
            row => row.Id == subtitle.SearchId && row.State == SubtitleSearchState.Syncing, cancellationToken);
        if (syncing)
        {
            return Result.Failure(InProgress);
        }

        if (!subtitle.IsSynced)
        {
            return Result.Failure(NotSynced);
        }

        var synced = subtitle.Path;
        var now = DateTimeOffset.UtcNow;
        await unitOfWork.ExecuteAsync(async token =>
        {
            subtitle.UseOriginal();
            dbContext.SyncDecisions.Add(SubtitleSyncDecision.Unmeasured(
                subtitle.Id, SubtitleSyncOutcome.Reverted, "Reverted to the downloaded file by an administrator.", now));
            await dbContext.SaveChangesAsync(token);
        }, cancellationToken);

        try
        {
            await fileStore.DeleteSyncedAsync(synced, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing points at it any more; a leftover copy is untidy, not wrong.
            logger.LogWarning(ex, "The synchronized copy of subtitle {SubtitleId} could not be removed.", subtitle.Id);
        }

        return Result.Success();
    }
}
